using System.Collections.Concurrent;
using System.Text;

using Spectre.Console;

namespace ContentFinder;

class Program
{
    private static CancellationTokenSource _cts = new();

    static async Task Main(string[] args)
    {
        Console.CancelKeyPress += (o, e) =>
        {
            e.Cancel = true;
            _cts.Cancel();
        };

        AnsiConsole.Write(new FigletText("Content Finder"));
        AnsiConsole.Write(new Paragraph("This program will recursively scan every sub-directory and read every files' contents in search for a specific string." +
                                        "As such, this can be a resource intensive application." +
                                        "Files above 1 GB are skipped." +
                                        $"{Environment.NewLine}", new Style(Color.DarkOrange)));

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

            AnsiConsole.Write(new Text("You can press CTRL+C to stop early. ", new Style(Color.Cyan1)));
            AnsiConsole.Write(new Text("Press any key to begin...", new Style(Color.Cyan1)));
            Console.ReadKey();

            if (!_cts.IsCancellationRequested)
                _cts.Cancel();

            _cts = new CancellationTokenSource();

            await AnsiConsole.Progress()
                .AutoClear(true)
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

                    while (!_cts.IsCancellationRequested && directoriesToScan.TryDequeue(out var currentDirectory))
                    {
                        tasks.Add(Task.Run(async () =>
                        {
                            try
                            {
                                var subDirectories = Directory.EnumerateDirectories(currentDirectory);
                                foreach (var subDirectory in subDirectories)
                                {
                                    if (_cts.IsCancellationRequested)
                                        break;

                                    directoriesToScan.Enqueue(subDirectory);
                                    directoriesToScanProgress.TryAdd(subDirectory, null);
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
                        }));

                        tasks.Add(Task.Run(async () =>
                            {
                                if (!directoriesToScanProgress.TryGetValue(currentDirectory, out var directoryProgress))
                                    return;

                                _ = directoriesToScanProgress.TryRemove(currentDirectory, out _);
                                _ = directoriesToScanProgress.TryAdd(currentDirectory, directoryProgress = ctx.AddTask(currentDirectory));

                                var foundMatches = false;

                                directoryProgress.StartTask();

                                try
                                {
                                    var files = Directory.EnumerateFiles(currentDirectory, "*").ToArray();

                                    for (var i = 0; i < files.Length; i++)
                                    {
                                        if (_cts.IsCancellationRequested)
                                            break;

                                        var file = files[i];

                                        var fileInfo = new FileInfo(file);

                                        const long GIGABYTE = 1_073_741_824;
                                        if (fileInfo.Length > GIGABYTE)
                                            continue;

                                        using var streamReader = new StreamReader(file);

                                        while (!_cts.IsCancellationRequested && await streamReader.ReadLineAsync() is { } line)
                                        {
                                            if (!line.Contains(search, StringComparison.Ordinal))
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
                        tasks.Clear();
                    }
                });

            AnsiConsole.Write(new Text($"Scan finished.{Environment.NewLine}", new Style(Color.Cyan1)));

            ShowResults(search, matchingFiles);

            AnsiConsole.Write(new Text($"Press any key to start over...{Environment.NewLine}", new Style(Color.Cyan1)));
            Console.ReadKey();
        }
    }

    private static string LimitContentPeek(string search, string line)
    {
        int maxLen = search.Length * 2;
        int startIndex = line.IndexOf(search, StringComparison.InvariantCultureIgnoreCase);

        int endIndex = startIndex + search.Length;

        // Calculate the start and end indices for the substring to be returned.
        int startPeek = Math.Max(0, startIndex - maxLen / 2);
        int endPeek = Math.Min(line.Length, endIndex + maxLen / 2);

        var result = line[startPeek..endPeek].Replace(' ', '.');
        return result;
    }

    private static void ShowResults(string searchString, IEnumerable<(string, string)> matchingFiles)
    {
        var table = new Table().Title($"Files containing \"{searchString}\"").BorderColor(Color.Grey);

        table.AddColumn(new TableColumn("File").Centered());
        table.AddColumn(new TableColumn("Full Path").LeftAligned());
        table.AddColumn(new TableColumn("Content Peek").LeftAligned());

        foreach (var fileContentPair in matchingFiles)
        {
            try
            {
                table.AddRow(
                    fileContentPair.Item1[(fileContentPair.Item1.LastIndexOf('\\') + 1)..],
                    fileContentPair.Item1,
                    fileContentPair.Item2);

            }
            catch (Exception e)
            {
                AnsiConsole.Write(new Text($"Match found in: {fileContentPair.Item1}, but failed to display.", new Style(Color.Red)));
                AnsiConsole.WriteException(e);
            }
        }

        AnsiConsole.Write(table);
    }
}
