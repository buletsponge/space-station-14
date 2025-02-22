﻿using Content.Server.Administration;
using Robust.Shared.Console;
using Robust.Shared.Localization;

namespace Content.Server.Utility.Commands
{
    [AnyCommand]
    class EchoCommand : IConsoleCommand
    {
        public string Command => "echo";

        public string Description => Loc.GetString("echo-command-description");

        public string Help => Loc.GetString("echo-command-help-text", ("command", Command));

        public void Execute(IConsoleShell shell, string argStr, string[] args)
        {
            if (argStr.Length > Command.Length)
                shell.WriteLine(argStr.Substring(Command.Length+1));
            return;
        }
    }
}
