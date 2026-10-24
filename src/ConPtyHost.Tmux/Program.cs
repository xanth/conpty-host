using System.Globalization;
using ConPtyHost.Core.Ipc;
using ConPtyHost.Core.Pty;
using ConPtyHost.Core.Server;

// `tmux __server -S path` is the detached server a client started; anything
// else is a client invocation, exactly as tmux would be called.
if (args is [ServerLauncher.ServerArgument, "-S", var socketPath])
{
    string logPath = socketPath + ".log";
    void Log(Exception failure) =>
        File.AppendAllText(logPath, string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O} {failure}\n"));

    await using TmuxServer server = new(PseudoTerminal.Start);
    try
    {
        await ServerHost.RunAsync(socketPath, server, Log);
        return 0;
    }
    catch (Exception failure)
    {
        Log(failure);
        return 1;
    }
}

string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate the tmux executable.");
TmuxClient client = new(socket => ServerLauncher.StartAsync(executable, socket));
return await client.RunAsync(args, Console.OpenStandardInput(), Console.OpenStandardOutput(), Console.Error);
