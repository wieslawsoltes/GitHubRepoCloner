using System;
using System.CommandLine;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.Json;
using System.Diagnostics;
using System.Threading;

namespace GitHubRepoCloner
{
    class Program
    {
        static async Task<int> Main(string[] args)
        {
            // Create the root command
            var rootCommand = new RootCommand("Clone all GitHub repositories to a specified directory.");

            // Add options
            var directoryOption = new Option<DirectoryInfo>(
                new[] { "-d", "--directory" },
                description: "The target directory where repositories will be cloned.")
            {
                IsRequired = true,
            };

            var usernameOption = new Option<string>(
                new[] { "-u", "--username" },
                description: "Your GitHub username.")
            {
                IsRequired = true,
            };

            var tokenOption = new Option<string>(
                new[] { "-t", "--token" },
                description: "Your GitHub personal access token.")
            {
                IsRequired = true,
            };

            var sourceOption = new Option<bool>(
                new[] { "--source" },
                "Clone source (non-forked) repositories.");

            var forksOption = new Option<bool>(
                new[] { "--forks" },
                "Clone forked repositories.");

            var shallowOption = new Option<bool>(
                new[] { "--shallow" },
                "Perform a shallow clone (clone only the latest commit).");

            var parallelismOption = new Option<int?>(
                new[] { "-p", "--parallelism" },
                () => Environment.ProcessorCount,
                "Maximum number of parallel clone operations. Defaults to the number of logical processors.");

            var retriesOption = new Option<int>(
                new[] { "--retries" },
                () => 3,
                "Number of times to retry cloning a repository on error.");

            rootCommand.AddOption(directoryOption);
            rootCommand.AddOption(usernameOption);
            rootCommand.AddOption(tokenOption);
            rootCommand.AddOption(sourceOption);
            rootCommand.AddOption(forksOption);
            rootCommand.AddOption(shallowOption);
            rootCommand.AddOption(parallelismOption);
            rootCommand.AddOption(retriesOption);

            rootCommand.SetHandler(async (DirectoryInfo directory, string username, string token, bool source, bool forks, bool shallow, int? parallelism, int retries) =>
            {
                await CloneRepositories(directory, username, token, source, forks, shallow, parallelism.Value, retries);
            }, directoryOption, usernameOption, tokenOption, sourceOption, forksOption, shallowOption, parallelismOption, retriesOption);

            return await rootCommand.InvokeAsync(args);
        }

        static async Task CloneRepositories(DirectoryInfo targetDirectory, string username, string token, bool cloneSource, bool cloneForks, bool shallowClone, int maxParallelism, int maxRetries)
        {
            // If neither source nor forks is specified, default to cloning both
            if (!cloneSource && !cloneForks)
            {
                cloneSource = true;
                cloneForks = true;
            }

            // Ensure target directory exists
            if (!targetDirectory.Exists)
            {
                targetDirectory.Create();
            }

            // Get repositories
            var repositories = await GetRepositories(username, token);

            // Filter repositories based on forked status
            var filteredRepos = new List<Repository>();

            foreach (var repo in repositories)
            {
                if (repo.Fork && cloneForks)
                {
                    filteredRepos.Add(repo);
                }
                else if (!repo.Fork && cloneSource)
                {
                    filteredRepos.Add(repo);
                }
            }

            int totalRepos = filteredRepos.Count;
            int clonedCount = 0;
            object progressLock = new object();

            Console.WriteLine($"Total repositories to clone: {totalRepos}");

            // Clone repositories in parallel
            var options = new ParallelOptions
            {
                MaxDegreeOfParallelism = maxParallelism
            };

            await Parallel.ForEachAsync(filteredRepos, options, async (repo, cancellationToken) =>
            {
                var repoDir = Path.Combine(targetDirectory.FullName, repo.Name);
                if (Directory.Exists(repoDir))
                {
                    lock (progressLock)
                    {
                        clonedCount++;
                        Console.WriteLine($"[{clonedCount}/{totalRepos}] Repository '{repo.Name}' already exists. Skipping.");
                    }
                }
                else
                {
                    int attempt = 0;
                    bool success = false;
                    while (attempt < maxRetries && !success)
                    {
                        attempt++;
                        try
                        {
                            Console.WriteLine($"[{clonedCount + 1}/{totalRepos}] Cloning repository '{repo.Name}' (Attempt {attempt}/{maxRetries})...");
                            await CloneRepository(repo.CloneUrl, targetDirectory.FullName, username, token, repo.Private, shallowClone);
                            success = true;
                            lock (progressLock)
                            {
                                clonedCount++;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"Error cloning repository '{repo.Name}': {ex.Message}");
                            if (attempt >= maxRetries)
                            {
                                Console.Error.WriteLine($"Failed to clone repository '{repo.Name}' after {maxRetries} attempts.");
                            }
                            else
                            {
                                Console.WriteLine($"Retrying '{repo.Name}'...");
                            }
                        }
                    }
                }
            });

            Console.WriteLine($"Cloning completed. {clonedCount} out of {totalRepos} repositories cloned successfully.");
        }

        static async Task<List<Repository>> GetRepositories(string username, string token)
        {
            var repositories = new List<Repository>();
            using (var client = new HttpClient())
            {
                client.DefaultRequestHeaders.UserAgent.Add(new System.Net.Http.Headers.ProductInfoHeaderValue("GitHubRepoCloner", "1.0"));
                client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

                var page = 1;
                var perPage = 100;
                bool morePages = true;

                while (morePages)
                {
                    var url = $"https://api.github.com/user/repos?visibility=all&affiliation=owner&per_page={perPage}&page={page}";
                    var response = await client.GetAsync(url);

                    response.EnsureSuccessStatusCode();

                    var content = await response.Content.ReadAsStringAsync();

                    var repos = JsonSerializer.Deserialize<List<RepositoryJson>>(content);

                    if (repos.Count > 0)
                    {
                        repositories.AddRange(repos.ConvertAll(r => new Repository
                        {
                            Name = r.name,
                            Fork = r.fork,
                            CloneUrl = r.clone_url,
                            Private = r.@private
                        }));
                        page++;
                    }
                    else
                    {
                        morePages = false;
                    }
                }
            }

            return repositories;
        }

        static async Task CloneRepository(string cloneUrl, string targetDirectory, string username, string token, bool isPrivate, bool shallowClone)
        {
            string cloneUrlWithCredentials = cloneUrl;

            if (isPrivate)
            {
                // Warning about including credentials in the URL
                Console.WriteLine("Warning: Cloning a private repository. Credentials are being used in the clone URL. Ensure that no sensitive information is exposed.");

                // Encode username and token
                string encodedToken = Uri.EscapeDataString(token);
                string encodedUsername = Uri.EscapeDataString(username);

                // Insert credentials into clone URL
                var uriBuilder = new UriBuilder(cloneUrl);
                uriBuilder.UserName = encodedUsername;
                uriBuilder.Password = encodedToken;

                cloneUrlWithCredentials = uriBuilder.Uri.ToString();
            }

            var arguments = $"clone \"{cloneUrlWithCredentials}\"";

            if (shallowClone)
            {
                arguments += " --depth=1";
            }

            var processStartInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = targetDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using (var process = new Process())
            {
                process.StartInfo = processStartInfo;

                // Capture output and errors
                var outputBuilder = new System.Text.StringBuilder();
                var errorBuilder = new System.Text.StringBuilder();

                process.OutputDataReceived += (sender, e) => { if (e.Data != null) outputBuilder.AppendLine(e.Data); };
                process.ErrorDataReceived += (sender, e) => { if (e.Data != null) errorBuilder.AppendLine(e.Data); };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                await process.WaitForExitAsync();

                if (process.ExitCode != 0)
                {
                    throw new Exception($"Git clone failed with exit code {process.ExitCode}. Error: {errorBuilder}");
                }
            }
        }

        class Repository
        {
            public string Name { get; set; }
            public bool Fork { get; set; }
            public string CloneUrl { get; set; }
            public bool Private { get; set; }
        }

        class RepositoryJson
        {
            public string name { get; set; }
            public bool fork { get; set; }
            public string clone_url { get; set; }
            public bool @private { get; set; }
        }
    }
}
