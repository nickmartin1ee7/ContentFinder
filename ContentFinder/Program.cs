using System.Collections.Concurrent;
using System.Text;

using Spectre.Console;

namespace ContentFinder;

class Program
{
    static async Task Main(string[] args)
    {
        while (true)
        {
            var directoryPath = AnsiConsole
                .Prompt(new TextPrompt<string>("Enter directory path:")
                    .Validate(Directory.Exists)
                    .PromptStyle(new Style(Color.Blue)));

            // Correct directory path
            var newDirectoryPath = new DirectoryInfo(directoryPath).FullName;

            if (newDirectoryPath != directoryPath)
            {
                AnsiConsole.Write(new Text($"Correcting directory path to: {newDirectoryPath}{Environment.NewLine}",
                    new Style(Color.Grey)));
                directoryPath = newDirectoryPath;
            }

            var searchString = AnsiConsole
                .Prompt(new TextPrompt<string>("Enter search string:")
                    .PromptStyle(new Style(Color.Blue)));

            var matchingFiles = new ConcurrentBag<string>();
            var tasks = new List<Task>();
            var directoriesToScan = new ConcurrentQueue<string>();

            var prefix = $"Searching for \"{searchString}\"...";

            await AnsiConsole.Status()
                .StartAsync(prefix, async ctx =>
                {
                    // Enqueue root directory
                    directoriesToScan.Enqueue(directoryPath);

                    while (directoriesToScan.TryDequeue(out var currentDirectory))
                    {
                        try
                        {
                            var subDirectories = Directory.EnumerateDirectories(currentDirectory);
                            foreach (var subDirectory in subDirectories)
                            {
                                directoriesToScan.Enqueue(subDirectory);
                            }
                        }
                        catch (IOException)
                        {
                            // Suppress
                        }
                        catch (UnauthorizedAccessException ex)
                        {
                            // Suppress
                        }
                        catch (Exception ex)
                        {
                            AnsiConsole.WriteException(ex);
                        }

                        tasks.Add(Task.Run(async () =>
                        {
                            ctx.Status = $"{prefix} {currentDirectory}";
                            ctx.Refresh();

                            var foundMatches = false;

                            try
                            {
                                foreach (var file in Directory.EnumerateFiles(currentDirectory, "*"))
                                {
                                    using var streamReader = new StreamReader(file);

                                    while (await streamReader.ReadLineAsync() is { } line)
                                    {
                                        if (!line.Contains(searchString))
                                            continue;

                                        matchingFiles.Add(file);
                                        foundMatches = true;
                                        break;
                                    }
                                }
                            }
                            catch (IOException)
                            {
                                // Suppress
                            }
                            catch (UnauthorizedAccessException ex)
                            {
                                // Suppress
                            }
                            catch (Exception ex)
                            {
                                AnsiConsole.WriteException(ex);
                            }

                            var outputSb = new StringBuilder($"Scanned: {currentDirectory}");

                            if (foundMatches)
                            {
                                outputSb.Append(" (FOUND MATCHES)");
                                outputSb.Append(Environment.NewLine);
                                AnsiConsole.Write(new Text(outputSb.ToString(), new Style(Color.Green)));
                            }
                            else
                            {
                                outputSb.Append(Environment.NewLine);
                                AnsiConsole.Write(new Text(outputSb.ToString(), new Style(Color.Grey)));
                            }

                            ctx.Refresh();
                        }));

                        Task.WaitAll(tasks.ToArray());
                    }
                });

            ShowResults(searchString, matchingFiles);

            AnsiConsole.WriteLine("Press any key to start over.");
            Console.ReadKey();
        }
    }

    private static void ShowResults(string searchString, IEnumerable<string> matchingFiles)
    {
        var table = new Table().Title($"Files containing \"{searchString}\"").BorderColor(Color.Grey);

        table.AddColumn(new TableColumn("File").Centered());
        table.AddColumn(new TableColumn("Full Path").Centered());

        foreach (var fullPathFile in matchingFiles)
        {
            table.AddRow(fullPathFile[(fullPathFile.LastIndexOf('\\') + 1)..], fullPathFile);
        }

        AnsiConsole.Write(table);
    }
}
