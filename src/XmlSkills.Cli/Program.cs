using XmlSkills.Cli;
using XmlSkills.Core;

CliApplication app = new(DefaultRegistryFactory.Create());
return await app.RunAsync(args, Console.Out, Console.Error, CancellationToken.None).ConfigureAwait(false);
