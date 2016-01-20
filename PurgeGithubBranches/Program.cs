using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Serialization;
using CommandLine;
using CommandLine.Text;
using Octokit;
using ProductHeaderValue = Octokit.ProductHeaderValue;

namespace PurgeGithubBranches
{
    internal class Options
    {
        [Option('t', "token", Required = true,
            HelpText = "specify github token")]
        public string Token { get; set; }

        [Option('o', "owner", Required = true,
            HelpText = "Github repository owner")]
        public string Owner { get; set; }

        [Option('r', "repo", Required = true,
            HelpText = "Github repository name")]
        public string Repo { get; set; }

        [Option('v', "verbose", Required = false,
            HelpText = "Display the deleted branches, and logs")]
        public bool Verbose { get; set; } = true;

        [Option('d', "dry", Required = false,
            HelpText = "Dry-run the command without actually deleting the branches, --verbose will be on in this mode")]
        public bool Dryrun { get; set; } = false;

        [Option('c', "confirm", Required = false,
            HelpText = "Confirm before delete")]
        public bool Confirm { get; set; } = false;
        [HelpOption]
        public string GetUsage()
        {
            return HelpText.AutoBuild(this,
                current => HelpText.DefaultParsingErrorsHandler(this, current));
        }
    }

    [Serializable]
    public class HistoryItem
    {
        public string BranchName { get; set; }
        public int PrNum { get; set; }
        public string Reason { get; set; }

        public override bool Equals(object obj)
        {
            if (obj == null)
            {
                return false;
            }

            if (ReferenceEquals(this, obj))
            {
                return true;
            }

            var hist = obj as HistoryItem;
            return this.PrNum == hist?.PrNum;
        }

        public override int GetHashCode()
        {
            return PrNum.GetHashCode();
        }
    }

    [Serializable]
    public class History
    {
        public HashSet<HistoryItem> Items;

        private static string AppData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "nicologies");
        private static string HistFile = Path.Combine(AppData, "purgeprbranch.xml");

        static History()
        {
            if (!Directory.Exists(AppData))
            {
                Directory.CreateDirectory(AppData);
            }
        }
        public static History Load()
        {
            if (File.Exists(HistFile))
            {
                using (var stream = File.OpenRead(HistFile))
                {
                    return new XmlSerializer(typeof (History)).Deserialize(stream) as History;
                }
            }
            else
            {
                return new History {Items = new HashSet<HistoryItem>()};
            }
        }

        private void Save()
        {
            using (var stream = File.OpenWrite(HistFile))
            {
                new XmlSerializer(typeof(History)).Serialize(stream, this);
            }
        }
    }

    internal class Program
    {
        static void Main(string[] args)
        {
            var options = new Options();
            if (!CommandLine.Parser.Default.ParseArguments(args, options))
            {
                Console.Error.WriteLine("Invalid parameters");
                Environment.Exit(-1);
            }

            var client = new GitHubClient(new ProductHeaderValue("PurgeGithubBranches"))
            {
                Credentials = new Credentials(options.Token)
            };

            var pullrequests =
                client.PullRequest.GetAllForRepository(options.Owner, options.Repo, new PullRequestRequest()
                {
                    State = ItemState.Closed,
                    SortDirection = SortDirection.Ascending,
                    SortProperty = PullRequestSort.Created
                }).Result;


            var history = History.Load();

            foreach (var pr in pullrequests)
            {
                if (history.Items.Any(r => r.PrNum == pr.Number))
                {
                    if (options.Verbose)
                    {
                        Console.WriteLine($"Skipping pull request ${pr.Number} as it was processed before");
                    }
                    continue;
                }
                var headRef = pr.Head.Ref;
                Branch branchCommit = null;
                try
                {
                    branchCommit = client.Repository.GetBranch(options.Owner, options.Repo, headRef).Result;
                }
                catch
                {
                    history.Items.Add(new HistoryItem()
                    {
                        Reason = "Cannot find branch",

                    })
                    continue;
                }

                IReadOnlyList<PullRequestCommit> prCommits = null;
                try
                {
                    prCommits = client.PullRequest.Commits(options.Owner, options.Repo, pr.Number).Result;
                }
                catch
                {
                    continue;
                }
                var emptyPullRequest = !prCommits.Any();
                if (emptyPullRequest || branchCommit.Commit.Sha == prCommits.Last().Sha)
                {
                    if (options.Dryrun)
                    {
                        Console.WriteLine(headRef);
                    }

                    else
                    {
                        if (options.Verbose)
                        {
                            Console.WriteLine("Deleting " + headRef);
                        }
                        if (options.Confirm)
                        {
                            Console.Write($"Are you sure to delete {headRef}, y/n(default is y)?");
                            var key = Console.ReadKey();
                            if (key.KeyChar != 'y' && key.Key != ConsoleKey.Enter)
                            {
                                continue;
                            }
                        }
                        var task = client.GitDatabase.Reference.Delete(options.Owner, options.Repo, "heads/" + headRef);
                        task.Wait();
                        if (options.Verbose)
                        {
                            Console.WriteLine("Deleted " + headRef);
                        }
                    }
                }
            }
            if (Debugger.IsAttached)
            {
                Console.ReadKey();
            }
        }
    }
}
