using System.Buffers;
using System.Collections.Concurrent;
using System.Text;

using Spectre.Console;

namespace ContentFinder;

public static class Program
{
    private static CancellationTokenSource s_cts = new();
    private static Dictionary<Color, Style> s_styleCache = new()
    {
        { Color.Blue, new(Color.Blue) },
        { Color.Grey, new(Color.Grey) },
        { Color.DarkOrange, new(Color.DarkOrange) },
        { Color.Cyan1, new(Color.Cyan1) },
        { Color.Green, new(Color.Green) },
        { Color.Red, new(Color.Red) },
    };

    public static async Task Main(string[] args)
    {
        Console.CancelKeyPress += OnCancelEventHandler;
        Console.Title = "Content Finder";

        PrintGreeting();

        while (true)
        {
            s_cts.Cancel();

            var rootDirectoryPath = PromptUser(out var search);
            var searchTermUpper = search.ToUpperInvariant();
            var searchTermLower = search.ToLowerInvariant();
            var maxPeekSize = search.Length * 3;

            var matchingFiles = new ConcurrentBag<(string fileName, string content)>();
            var tasks = new List<Task>();
            var directoriesToScan = new ConcurrentQueue<string>();
            var directoriesToScanProgress = new ConcurrentDictionary<string, ProgressTask?>();

            PrintPrepareToStart();

            Console.ReadKey();

            s_cts = new CancellationTokenSource();

            await AnsiConsole.Progress()
                .AutoClear(true)
                //.HideCompleted(true)
                .Columns(new ProgressColumn[]
                {
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                    new ElapsedTimeColumn(),
                    new SpinnerColumn(),
                })
                .StartAsync(PrimaryProcess);

            if (!s_cts.IsCancellationRequested)
            {
                s_cts.Cancel();
            }

            PrintScanFinished();

            ShowResults(search, matchingFiles);

            PrintRestartPrompt();

            Console.ReadKey();

            Task PrimaryProcess(ProgressContext ctx)
            {
                // Enqueue root directory
                directoriesToScan.Enqueue(rootDirectoryPath);
                directoriesToScanProgress.TryAdd(rootDirectoryPath, null);

                while (!s_cts.IsCancellationRequested
                    && directoriesToScan.TryDequeue(out var currentDirectory))
                {

                    // Find subdirectories
                    tasks.Add(Task.Run(() =>
                    {
                        try
                        {
                            var subDirectories = Directory.EnumerateDirectories(currentDirectory);
                            foreach (var subDirectory in subDirectories)
                            {
                                if (s_cts.IsCancellationRequested)
                                {
                                    break;
                                }

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

                    // Scan contents of currently dequeued directory
                    tasks.Add(Task.Run(async () =>
                    {
                        // Don't scan already in-progress directories
                        if (!directoriesToScanProgress.TryGetValue(currentDirectory, out var directoryProgress))
                        {
                            return;
                        }

                        _ = directoriesToScanProgress.TryRemove(currentDirectory, out _);
                        _ = directoriesToScanProgress.TryAdd(currentDirectory, directoryProgress = ctx.AddTask(currentDirectory));

                        var foundMatches = false;

                        directoryProgress.StartTask();

                        try
                        {
                            var files = Directory.EnumerateFiles(currentDirectory, "*").ToArray();

                            for (var i = 0; i < files.Length; i++)
                            {
                                if (s_cts.IsCancellationRequested)
                                {
                                    break;
                                }

                                var file = files[i];

                                var fileInfo = new FileInfo(file);

                                using var streamReader = new StreamReader(file);

                                const int bufferSize = 1024;
                                var rentedBuffer = ArrayPool<char>.Shared.Rent(bufferSize);
                                var peekContent = new Queue<char>(maxPeekSize);
                                Array.Clear(rentedBuffer);

                                // Search Term Index - Scoped before the while loop to resume peaking a file across multiple buffers
                                var resumeSearchCharIndex = 0;
                                var readBytes = -1;

                                while (!s_cts.IsCancellationRequested
                                    && (readBytes = await streamReader.ReadBlockAsync(rentedBuffer, 0, bufferSize)) > 0)
                                {
                                    bool isMatchInChunk = false;
                                    var lastMatchChunkIndex = -1;

                                    for (int j = 0; j <= readBytes; j++)
                                    {
                                        if (readBytes == j)
                                        {
                                            // Overflowed chunk, the next piece is in another chunk
                                            isMatchInChunk = false;
                                            break;
                                        }

                                        var nextChar = rentedBuffer[j];
                                        if (nextChar.Equals(searchTermUpper[resumeSearchCharIndex])
                                            || nextChar.Equals(searchTermLower[resumeSearchCharIndex]))
                                        {
                                            // First match in chunk
                                            if (peekContent.Count == 0
                                                && j - search.Length >= 0)
                                            {
                                                // Add the previous peek
                                                for (int k = 0; k < search.Length; k++)
                                                {
                                                    var nextRewindIndex = j - search.Length + k;
                                                    var rewindChar = rentedBuffer[nextRewindIndex];
                                                    EnqueueFormattedCharacter(peekContent, rewindChar);
                                                }
                                            }

                                            peekContent.Enqueue(nextChar);
                                            lastMatchChunkIndex = j;
                                            resumeSearchCharIndex++;
                                            isMatchInChunk = true;

                                            // Match complete
                                            if (resumeSearchCharIndex >= search.Length)
                                            {
                                                break;
                                            }
                                        }
                                        else if (isMatchInChunk) // We were matching, but no longer
                                        {
                                            peekContent.Clear();
                                            lastMatchChunkIndex = -1;
                                            resumeSearchCharIndex = 0;
                                            isMatchInChunk = false;
                                            break;
                                        }
                                    }

                                    // False positive, did not match search term. Keep searching chunk!
                                    if (!isMatchInChunk)
                                    {
                                        continue;
                                    }

                                    // Add the forward peek
                                    for (int x = lastMatchChunkIndex + 1; x < readBytes && peekContent.Count < maxPeekSize; x++)
                                    {
                                        var forwardChar = rentedBuffer[x];
                                        EnqueueFormattedCharacter(peekContent, forwardChar);
                                    }

                                    var resultPeek = string.Join("", peekContent);
                                    matchingFiles.Add((file, resultPeek));
                                    foundMatches = true;
                                    break;
                                }

                                ArrayPool<char>.Shared.Return(rentedBuffer);
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
                            AnsiConsole.Write(new Text(outputSb.ToString(), s_styleCache[Color.Green]));
                        }
#if DEBUG // Can be rather verbose and jittery
                        else
                        {
                            outputSb.Append(Environment.NewLine);
                            AnsiConsole.Write(new Text(outputSb.ToString(), s_styleCache[Color.Grey]));
                        }
#endif

                        directoryProgress.Increment(100);
                        directoryProgress.StopTask();
                    }));

                    Task.WaitAll(tasks.ToArray());
                    tasks.Clear();
                }

                return Task.CompletedTask;
            }
        }
    }

    private static void EnqueueFormattedCharacter(Queue<char> characterQueue, char unformattedChar)
    {
        characterQueue.Enqueue(unformattedChar switch
        {
            '\r' => '.',
            '\n' => '.',
            '\t' => '.',
            _ => unformattedChar
        });
    }

    private static void PrintRestartPrompt()
    {
        AnsiConsole.Write(new Text($"Press any key to start over...{Environment.NewLine}", s_styleCache[Color.Cyan1]));
    }

    private static void PrintScanFinished()
    {
        AnsiConsole.Write(new Text($"Scan finished.{Environment.NewLine}", s_styleCache[Color.Cyan1]));
    }

    private static void PrintPrepareToStart()
    {
        AnsiConsole.Write(new Text("You can press CTRL+C to stop early. ", s_styleCache[Color.Cyan1]));
        AnsiConsole.Write(new Text($"Press any key to begin...{Environment.NewLine}", s_styleCache[Color.Cyan1]));
    }

    private static string PromptUser(out string search)
    {
        var directoryPath = AnsiConsole
            .Prompt(new TextPrompt<string>("Enter directory path:")
                .Validate(Directory.Exists)
                .PromptStyle(s_styleCache[Color.Blue]));

        // Correct directory path
        var newDirectoryPath = new DirectoryInfo(directoryPath).FullName;

        if (newDirectoryPath != directoryPath)
        {
            AnsiConsole.Write(new Text($"Correcting directory path to: {newDirectoryPath}{Environment.NewLine}",
                s_styleCache[Color.Grey]));
            directoryPath = newDirectoryPath;
        }

        search = AnsiConsole
            .Prompt(new TextPrompt<string>("Enter search string:")
                .PromptStyle(s_styleCache[Color.Blue]));
        return directoryPath;
    }

    private static void PrintGreeting()
    {
        AnsiConsole.Write(new FigletText("Content Finder"));
        AnsiConsole.Write(new Paragraph(
            "This program will recursively scan every sub-directory and read every files' contents in search for a specific string." +
            $"{Environment.NewLine}", s_styleCache[Color.DarkOrange]));
    }

    private static void OnCancelEventHandler(object? o, ConsoleCancelEventArgs? e)
    {
        e.Cancel = !s_cts.IsCancellationRequested; // Don't terminate if token is in use; we need to clean-up.
        s_cts.Cancel();

        if (!e.Cancel)
        {
            Environment.Exit(0);
        }
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
                    new Text(fileContentPair.Item1[(fileContentPair.Item1.LastIndexOf('\\') + 1)..]),
                    new Text(fileContentPair.Item1),
                    new Text(fileContentPair.Item2, s_styleCache[Color.Grey]));

            }
            catch (Exception e)
            {
                AnsiConsole.Write(new Text($"Match found in: {fileContentPair.Item1}, but failed to display.", s_styleCache[Color.Red]));
                AnsiConsole.WriteException(e);
            }
        }

        AnsiConsole.Write(table);
    }
}
