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

            var search = AnsiConsole
                .Prompt(new TextPrompt<string>("Enter search string:")
                    .PromptStyle(new Style(Color.Blue)));

            var matchingFiles = new ConcurrentBag<(string fileName, string content)>();
            var tasks = new List<Task>();
            var directoriesToScan = new ConcurrentQueue<string>();
            var directoriesToScanProgress = new ConcurrentDictionary<string, ProgressTask>();

            await AnsiConsole.Progress()
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new ElapsedTimeColumn(),
                    new SpinnerColumn(),
                })
                .StartAsync(async ctx =>
                {
                    // Enqueue root directory
                    directoriesToScan.Enqueue(directoryPath);
                    directoriesToScanProgress.TryAdd(directoryPath, ctx.AddTask(directoryPath));

                    while (directoriesToScan.TryDequeue(out var currentDirectory))
                    {
                        try
                        {
                            var subDirectories = Directory.EnumerateDirectories(currentDirectory);
                            foreach (var subDirectory in subDirectories)
                            {
                                directoriesToScan.Enqueue(subDirectory);
                                directoriesToScanProgress.TryAdd(subDirectory, ctx.AddTask(subDirectory));
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
                                if (!directoriesToScanProgress.TryGetValue(currentDirectory, out var directoryProgress))
                                    return;

                                var foundMatches = false;
                                directoryProgress.StartTask();

                                try
                                {
                                    var files = Directory.EnumerateFiles(currentDirectory, "*").ToArray();

                                    for (var i = 0; i < files.Length; i++)
                                    {
                                        var file = files[i];
                                        using var streamReader = new StreamReader(file);

                                        while (await streamReader.ReadLineAsync() is { } line)
                                        {
                                            if (!line.Contains(search))
                                                continue;

                                            matchingFiles.Add((file, LimitContentPeek(search, line)));
                                            foundMatches = true;
                                            break;
                                        }

                                        directoryProgress.Increment(i / (double)files.Length * 100);
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
#if DEBUG // Can be rather verbose and jittery
                                else
                                {
                                    outputSb.Append(Environment.NewLine);
                                    AnsiConsole.Write(new Text(outputSb.ToString(), new Style(Color.Grey)));
                                }
#endif

                                directoryProgress.Increment(100);
                                directoryProgress.StopTask();
                            }));

                        Task.WaitAll(tasks.ToArray());
                    }
                });

            ShowResults(search, matchingFiles);

            AnsiConsole.WriteLine("Press any key to start over.");
            Console.ReadKey();
        }
    }

    private static string LimitContentPeek(string search, string line)
    {
        int maxLen = search.Length * 2;
        int startIndex = line.IndexOf(search, StringComparison.InvariantCultureIgnoreCase);

        if (startIndex < 0)
        {
            // If the search term is not found in the line, return the first maxLen characters of the line.
            // Shouldn't happen though.
            return line.Substring(0, Math.Min(maxLen, line.Length)).Replace(' ', '.');
        }

        int endIndex = startIndex + search.Length;

        // Calculate the start and end indices for the substring to be returned.
        int startPeek = Math.Max(0, startIndex - maxLen / 2);
        int endPeek = Math.Min(line.Length, endIndex + maxLen / 2);

        return line[startPeek..endPeek].Replace(' ', '.');
    }

    private static void ShowResults(string searchString, IEnumerable<(string, string)> matchingFiles)
    {
        var table = new Table().Title($"Files containing \"{searchString}\"").BorderColor(Color.Grey);

        table.AddColumn(new TableColumn("File").Centered());
        table.AddColumn(new TableColumn("Full Path").LeftAligned());
        table.AddColumn(new TableColumn("Content Peek").LeftAligned());

        foreach (var fileContentPair in matchingFiles)
        {
            table.AddRow(
                fileContentPair.Item1[(fileContentPair.Item1.LastIndexOf('\\') + 1)..],
                fileContentPair.Item1,
                fileContentPair.Item2);
        }

        AnsiConsole.Write(table);
    }
}
