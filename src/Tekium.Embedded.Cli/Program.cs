// Copyright (c) Tekium. Licensed under the MIT License.

using Tekium.Embedded.Cli;

using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cancellation.Cancel();
};

return await CliApp.RunAsync(args, Console.In, Console.Out, Console.Error, Environment.GetEnvironmentVariable, cancellation.Token)
    .ConfigureAwait(false);
