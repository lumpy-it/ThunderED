﻿#region License Information (GPL v3)

/*
    Copyright (c) Jaex

    This program is free software; you can redistribute it and/or
    modify it under the terms of the GNU General Public License
    as published by the Free Software Foundation; either version 2
    of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful,
    but WITHOUT ANY WARRANTY; without even the implied warranty of
    MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
    GNU General Public License for more details.

    You should have received a copy of the GNU General Public License
    along with this program; if not, write to the Free Software
    Foundation, Inc., 51 Franklin Street, Fifth Floor, Boston, MA  02110-1301, USA.

    Optionally you can also view the license at <http://www.gnu.org/licenses/>.
*/

#endregion License Information (GPL v3)

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace ThunderED.Classes.IRC
{
    public class IRC : IDisposable
    {
        public const int DEFAULT_PORT = 6667;
        public const int DEFAULT_PORT_SSL = 6697;

        public delegate void IRCOutputEventHandler(MessageInfo messageInfo);
        public event IRCOutputEventHandler Output;

        public delegate Task IRCErrorEventHandler(Exception e);
        public event IRCErrorEventHandler ErrorOutput;

        public delegate void IRCEventHandler();
        public event IRCEventHandler Connected, Disconnected;

        public delegate void IRCMessageEventHandler(UserInfo userInfo, string channel, string message);
        public event IRCMessageEventHandler Message;

        public delegate void IRCUserEventHandler(UserInfo userInfo);
        public event IRCUserEventHandler UserQuit, WhoisResult;

        public delegate Task IRCUserChannelEventHandler(UserInfo userInfo, string channel);
        public event IRCUserChannelEventHandler UserJoined, UserLeft;

        public delegate void IRCUserNickChangedEventHandler(UserInfo userInfo, string newNick);
        public event IRCUserNickChangedEventHandler UserNickChanged;

        public delegate void IRCUserKickedEventHandler(UserInfo userInfo, string channel, string kickedNick);
        public event IRCUserKickedEventHandler UserKicked;

        public IRCSettings Settings { get; }
        public bool IsWorking { get; private set; }
        public bool IsConnected { get; private set; }
        public string CurrentNickname { get; private set; }

        private TcpClient _tcp;
        private StreamReader _streamReader;
        private StreamWriter _streamWriter;
        private bool _disconnecting;
        private Timer _pingTimer;
        private string _lastPingServer;
        private readonly List<UserInfo> _userList = new List<UserInfo>();


        public IRC(IRCSettings settings)
        {
            Settings = settings;
        }

        public async void Dispose()
        {
            await Disconnect();
        }

        public async Task Connect()
        {
            if (IsWorking)
                return;

            try
            {
                IsWorking = true;
                IsConnected = false;
                _disconnecting = false;

                var port = Settings.ircModule.Port;

                if (port <= 0)
                    port = Settings.ircModule.UseSSL ? DEFAULT_PORT_SSL : DEFAULT_PORT;

                _tcp = new TcpClient()
                {
                    NoDelay = true
                };

                await _tcp.ConnectAsync(Settings.ircModule.Server, port);

                Stream networkStream = _tcp.GetStream();

                if (Settings.ircModule.UseSSL)
                {
                    var sslStream = new SslStream(networkStream, false, (sender, certificate, chain, sslPolicyErrors) => true);
                    await sslStream.AuthenticateAsClientAsync(Settings.ircModule.Server);
                    networkStream = sslStream;
                }

                _streamReader = new StreamReader(networkStream);
                _streamWriter = new StreamWriter(networkStream) {AutoFlush = true};

                await Task.Run(() => StartReadThread());

                if (!string.IsNullOrEmpty(Settings.ircModule.Password))
                    await SetPassword(Settings.ircModule.Password);

                await SetUser(Settings.ircModule.Username, Settings.ircModule.Realname, Settings.ircModule.Invisible);
                await ChangeNickname(Settings.ircModule.Nickname);

                _pingTimer = new Timer(PingTimerCallback, null, 30000, 30000);
                OnOutput(new MessageInfo($"Connected to IRC server {Settings.ircModule.Server}:{Settings.ircModule.Port}"));
            }
            catch (Exception e)
            {
                IsWorking = false;
                OnErrorOutput(e);
            }
        }

        public async Task Disconnect()
        {
            try
            {
                _disconnecting = true;
                if (IsWorking)
                    await Quit(Settings.ircModule.QuitReason);
                _streamReader?.Dispose();
                _streamWriter?.Dispose();
                _tcp?.Dispose();
            }
            catch (Exception e)
            {
                OnErrorOutput(e);
            }
        }

        public async Task Reconnect()
        {
            await Disconnect();
            await Connect();
        }

        private async void PingTimerCallback(object state)
        {
            if (!string.IsNullOrEmpty(_lastPingServer))
            {
                await SendPing(_lastPingServer);
            }
        }

        private async void StartReadThread()
        {
            try
            {
                string message;

                while ((message = await _streamReader.ReadLineAsync()) != null)
                {
                    try
                    {
                        var commandResult = await CheckCommand(message);

                        if (!commandResult)
                        {
                            break;
                        }
                    }
                    catch (Exception e)
                    {
                        OnErrorOutput(e);
                    }
                }
            }
            catch (Exception e)
            {
                if (!_disconnecting)
                {
                    OnErrorOutput(e);
                }
            }

            _pingTimer?.Dispose();

            IsWorking = IsConnected = false;

            OnDisconnected();

            if (!_disconnecting && Settings.ircModule.AutoReconnect)
            {
                await Task.Delay(Settings.ircModule.AutoReconnectDelay);

                await Reconnect();
            }
        }

        public async Task SendRawMessage(string message)
        {
            message = CheckUserCommands(message);

            await _streamWriter.WriteLineAsync(message);

            await CheckCommand(message);
        }

        private string CheckUserCommands(string message)
        {
            if (message.StartsWith("msg ", StringComparison.InvariantCultureIgnoreCase))
            {
                var parameters = Helpers.Helpers.SplitCommand(message, 3);

                if (parameters != null)
                {
                    return $"PRIVMSG {parameters[1]} :{parameters[2]}";
                }
            }

            return message;
        }

        private async Task<bool> CheckCommand(string message)
        {
            var messageInfo = MessageInfo.Parse(message);

            if (messageInfo.User.UserType == IRCUserType.Me)
            {
                messageInfo.User.Nickname = CurrentNickname;
            }

            var suppressOutput = (Settings.ircModule.SuppressMOTD && messageInfo.CheckCommand("375", //:sendak.freenode.net 375 Jaex :- sendak.freenode.net Message of the Day -
                "372", //:sendak.freenode.net 372 Jaex :- Welcome to sendak.freenode.net in Vilnius, Lithuania, EU.
                "376")) //:sendak.freenode.net 376 Jaex :End of /MOTD command.
                || (Settings.ircModule.SuppressPing && messageInfo.CheckCommand("PING", //PING :sendak.freenode.net
                "PONG")); //PONG :sendak.freenode.net

            if (!suppressOutput)
            {
                OnOutput(messageInfo);
            }

            if (messageInfo.CheckCommand("PING")) //PING :sendak.freenode.net
            {
                _lastPingServer = messageInfo.Message;
                await SendPong(messageInfo.Message);
            }
            else if (messageInfo.CheckCommand("376")) //:sendak.freenode.net 376 Jaex :End of /MOTD command.
            {
                IsConnected = true;

                OnConnected();

                foreach (var command in Settings.ircModule.ConnectCommands)
                {
                    await SendRawMessage(command);
                }

                if (!Settings.ircModule.AutoJoinWaitIdentify)
                {
                    await AutoJoinChannels();
                }
            }
            else if (messageInfo.CheckCommand("433")) //:sendak.freenode.net 433 * ShareX :Nickname is already in use.
            {
                if (!IsConnected && messageInfo.Parameters.Count >= 2)
                {
                    var nickname = !string.IsNullOrEmpty(Settings.ircModule.Nickname2) ? Settings.ircModule.Nickname2 : Settings.ircModule.Nickname + "_";

                    if (!messageInfo.Parameters[1].Equals(nickname, StringComparison.InvariantCultureIgnoreCase))
                    {
                        await ChangeNickname(nickname);
                    }
                }
            }
            else if (messageInfo.CheckCommand("PRIVMSG")) //:Jaex!Jaex@unaffiliated/jaex PRIVMSG #ShareX :test
            {
                await CheckMessage(messageInfo);
            }
            else if (messageInfo.CheckCommand("JOIN")) //:Jaex!Jaex@unaffiliated/jaex JOIN #ShareX or :Jaex!Jaex@unaffiliated/jaex JOIN :#ShareX
            {
                OnUserJoined(messageInfo.User, messageInfo.Parameters.Count > 0 ? messageInfo.Parameters[0] : messageInfo.Message);
            }
            else if (messageInfo.CheckCommand("PART")) //:Jaex!Jaex@unaffiliated/jaex PART #ShareX :"Leaving"
            {
                OnUserLeft(messageInfo.User, messageInfo.Parameters[0]);
            }
            else if (messageInfo.CheckCommand("QUIT")) //:Jaex!Jaex@unaffiliated/jaex QUIT :Client Quit
            {
                OnUserQuit(messageInfo.User);
            }
            else if (messageInfo.CheckCommand("NICK")) //:Jaex!Jaex@unaffiliated/jaex NICK :Jaex2
            {
                OnUserNickChanged(messageInfo.User, messageInfo.Message);
            }
            else if (messageInfo.CheckCommand("KICK")) //:Jaex!Jaex@unaffiliated/jaex KICK #ShareX Jaex2 :Jaex2
            {
                OnUserKicked(messageInfo.User, messageInfo.Parameters[0], messageInfo.Parameters[1]);

                if (Settings.ircModule.AutoRejoinOnKick && messageInfo.Parameters[1].Equals(CurrentNickname, StringComparison.InvariantCultureIgnoreCase))
                {
                    await JoinChannel(messageInfo.Parameters[0]);
                }
            }
            else if (messageInfo.CheckCommand("311", //:sendak.freenode.net 311 Jaex ShareX ~ShareX unaffiliated/sharex * :realname
                "319", //:sendak.freenode.net 319 Jaex ShareX :@#ShareX @#ShareX_Test
                "312", //:sendak.freenode.net 312 Jaex ShareX sendak.freenode.net :Vilnius, Lithuania, EU
                "671", //:sendak.freenode.net 671 Jaex ShareX :is using a secure connection
                "317", //:sendak.freenode.net 317 Jaex ShareX 39110 1440201914 :seconds idle, signon time
                "330", //:sendak.freenode.net 330 Jaex ShareX ShareX :is logged in as
                "318")) //:sendak.freenode.net 318 Jaex ShareX :End of /WHOIS list.
            {
                ParseWHOIS(messageInfo);
            }
            else if (messageInfo.CheckCommand("396")) //:sendak.freenode.net 396 Jaex unaffiliated/jaex :is now your hidden host (set by services.)
            {
                if (Settings.ircModule.AutoJoinWaitIdentify)
                {
                    await AutoJoinChannels();
                }
            }
            else if (messageInfo.CheckCommand("ERROR"))
            {
                return false;
            }

            return true;
        }

        private async Task CheckMessage(MessageInfo messageInfo)
        {
            var channel = messageInfo.Parameters[0];

            OnMessage(messageInfo.User, channel, messageInfo.Message);

            if (messageInfo.User.UserType == IRCUserType.User)
            {
                await HandleAutoResponse(channel, messageInfo.User.Nickname, messageInfo.Message.ToLowerInvariant());
            }
        }

        private async Task AutoJoinChannels()
        {
            foreach (string channel in Settings.ircModule.RelayChannels.Select(a=> a.IRC))
            {
                await JoinChannel(channel);
            }
        }

        #region Events

        protected void OnOutput(MessageInfo messageInfo)
        {
            Output?.Invoke(messageInfo);
        }

        protected void OnErrorOutput(Exception e)
        {
            ErrorOutput?.Invoke(e);
        }

        protected void OnConnected()
        {
            Connected?.Invoke();
        }

        protected void OnDisconnected()
        {
            Disconnected?.Invoke();
        }

        protected void OnMessage(UserInfo userInfo, string channel, string message)
        {
            Message?.Invoke(userInfo, channel, message);
        }

        protected void OnUserQuit(UserInfo userInfo)
        {
            UserQuit?.Invoke(userInfo);
        }

        protected void OnWhoisResult(UserInfo userInfo)
        {
            WhoisResult?.Invoke(userInfo);
        }

        protected void OnUserJoined(UserInfo userInfo, string channel)
        {
            UserJoined?.Invoke(userInfo, channel);
        }

        protected void OnUserLeft(UserInfo userInfo, string channel)
        {
            UserLeft?.Invoke(userInfo, channel);
        }

        protected void OnUserNickChanged(UserInfo userInfo, string newNick)
        {
            UserNickChanged?.Invoke(userInfo, newNick);
        }

        protected void OnUserKicked(UserInfo userInfo, string channel, string kickedNick)
        {
            UserKicked?.Invoke(userInfo, channel, kickedNick);
        }

        #endregion Events

        #region Commands

        // JOIN channel
        public async Task JoinChannel(string channel)
        {
            await SendRawMessage($"JOIN {channel}");
        }

        // TOPIC channel :message
        public async Task SetTopic(string channel, string message)
        {
            await SendRawMessage($"TOPIC {channel} :{message}");
        }

        // NOTICE channel/nick :message
        public async Task SendNotice(string channelnick, string message)
        {
            await SendRawMessage($"NOTICE {channelnick} :{message}");
        }

        // PRIVMSG channel/nick :message
        public async Task SendMessage(string message, string channel)
        {
            await SendRawMessage($"PRIVMSG {channel} :{message}");
        }

        // NICK nick
        public async Task ChangeNickname(string nick)
        {
            await SendRawMessage($"NICK {nick}");
            CurrentNickname = nick;
        }

        // PART channel :reason
        public async Task LeaveChannel(string channel, string reason = null)
        {
            await SendRawMessage(AddReason($"PART {channel}", reason));
        }

        // KICK nick :reason
        public async Task KickUser(string channel, string nick, string reason = null)
        {
            await SendRawMessage(AddReason($"KICK {channel} {nick}", reason));
        }

        // PASS password
        public async Task SetPassword(string password)
        {
            await SendRawMessage($"PASS {password}");
        }

        // USER username invisible * :realname
        public async Task SetUser(string username, string realname, bool invisible)
        {
            await SendRawMessage($"USER {username} {(invisible ? 8 : 0)} * :{realname}");
        }

        // WHOIS nick
        public async Task Whois(string nick, bool detailed = true)
        {
            var msg = $"WHOIS {nick}";
            if (detailed) msg += $" {nick}";
            await SendRawMessage(msg);
        }

        // QUIT :reason
        public async Task Quit(string reason = null)
        {
            await SendRawMessage(AddReason("QUIT", reason));
        }

        // PING :sendak.freenode.net
        public async Task SendPing(string server)
        {
            await SendRawMessage($"PING :{server}");
        }

        // PONG :sendak.freenode.net
        public async Task SendPong(string server)
        {
            await SendRawMessage($"PONG :{server}");
        }

        #endregion Commands

        private string AddReason(string command, string reason)
        {
            if (!string.IsNullOrEmpty(reason)) command += " :" + reason;
            return command;
        }

        private async Task<bool> HandleAutoResponse(string channel, string nick, string message)
        {
            if (Settings.ircModule.AutoResponse && nick != CurrentNickname)
            {
                foreach (var autoResponseInfo in Settings.ircModule.AutoResponseList)
                {
                    if (autoResponseInfo.CheckLastMatchTimer(Settings.ircModule.AutoResponseDelay) && autoResponseInfo.IsMatch(message, nick, CurrentNickname))
                    {
                        // Is it whisper?
                        if (!channel.StartsWith("#"))
                        {
                            channel = nick;
                        }

                        var response = autoResponseInfo.RandomResponse(nick, CurrentNickname);
                        await SendMessage(response, channel);

                        return true;
                    }
                }
            }

            return false;
        }

        private void ParseWHOIS(MessageInfo messageInfo)
        {
            UserInfo userInfo;

            switch (messageInfo.Command)
            {
                case "311": //:sendak.freenode.net 311 Jaex ShareX ~ShareX unaffiliated/sharex * :realname
                    if (messageInfo.Parameters.Count >= 4)
                    {
                        userInfo = new UserInfo
                        {
                            Nickname = messageInfo.Parameters[1],
                            Username = messageInfo.Parameters[2][1] == '~' ? messageInfo.Parameters[2].Substring(1) : messageInfo.Parameters[2],
                            Host = messageInfo.Parameters[3],
                            Realname = messageInfo.Message
                        };
                        _userList.Add(userInfo);
                    }
                    break;
                case "319": //:sendak.freenode.net 319 Jaex ShareX :@#ShareX @#ShareX_Test
                    if (messageInfo.Parameters.Count >= 2)
                    {
                        userInfo = FindUser(messageInfo.Parameters[1]);

                        if (userInfo != null)
                        {
                            userInfo.Channels.Clear();
                            userInfo.Channels.AddRange(messageInfo.Message.Split());
                        }
                    }
                    break;
                case "312": //:sendak.freenode.net 312 Jaex ShareX sendak.freenode.net :Vilnius, Lithuania, EU
                    if (messageInfo.Parameters.Count >= 3)
                    {
                        userInfo = FindUser(messageInfo.Parameters[1]);

                        if (userInfo != null)
                        {
                            userInfo.Server = messageInfo.Parameters[2];
                        }
                    }
                    break;
                case "671": //:sendak.freenode.net 671 Jaex ShareX :is using a secure connection
                    if (messageInfo.Parameters.Count >= 2)
                    {
                        userInfo = FindUser(messageInfo.Parameters[1]);

                        if (userInfo != null)
                        {
                            userInfo.SecureConnection = true;
                        }
                    }
                    break;
                case "317": //:sendak.freenode.net 317 Jaex ShareX 39110 1440201914 :seconds idle, signon time
                    if (messageInfo.Parameters.Count >= 4)
                    {
                        userInfo = FindUser(messageInfo.Parameters[1]);

                        if (userInfo != null)
                        {
                            if (int.TryParse(messageInfo.Parameters[2], out var idleTime))
                            {
                                userInfo.IdleTime = TimeSpan.FromSeconds(idleTime);
                            }

                            if (int.TryParse(messageInfo.Parameters[3], out var signOnTime))
                            {
                                userInfo.SignOnDate = Helpers.Helpers.UnixToDateTime(signOnTime).ToLocalTime();
                            }
                        }
                    }
                    break;
                case "330": //:sendak.freenode.net 330 Jaex ShareX ShareX :is logged in as
                    if (messageInfo.Parameters.Count >= 3)
                    {
                        userInfo = FindUser(messageInfo.Parameters[1]);

                        if (userInfo != null)
                        {
                            userInfo.Identity = messageInfo.Parameters[2];
                        }
                    }
                    break;
                case "318": //:sendak.freenode.net 318 Jaex ShareX :End of /WHOIS list.
                    if (messageInfo.Parameters.Count >= 2)
                    {
                        userInfo = FindUser(messageInfo.Parameters[1]);

                        if (userInfo != null)
                        {
                            _userList.Remove(userInfo);
                            OnWhoisResult(userInfo);
                        }
                    }
                    break;
            }
        }

        private UserInfo FindUser(string nickname)
        {
            return !string.IsNullOrEmpty(nickname) ? _userList.FirstOrDefault(user => user.Nickname == nickname) : null;
        }
    }
}