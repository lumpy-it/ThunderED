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

namespace ThunderED.Classes.IRC
{
    public enum IRCAutoResponseType
    {
        Contains,
        StartsWith,
        ExactMatch
    }

    public enum IRCUserType
    {
        Me,
        User,
        Server
    }

    public enum IRCColors
    {
        White = 0,
        Black = 1,
        Blue = 2,
        Green = 3,
        LightRed = 4,
        Brown = 5,
        Purple = 6,
        Orange = 7,
        Yellow = 8,
        LightGreen = 9,
        Cyan = 10,
        LightCyan = 11,
        LightBlue = 12,
        Pink = 13,
        Grey = 14,
        LightGrey = 15
    }
}