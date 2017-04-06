using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using FluentAssertions;
using GitHub.Unity;
using NSubstitute;
using NUnit.Framework;
using TestUtils;
using TestUtils.Events;

namespace UnitTests
{
    [TestFixture]
    class RepositoryManagerTests
    {
        [SetUp]
        public void SetUp()
        {
            NPathFileSystemProvider.Current =
                SubstituteFactory.CreateFileSystem(new CreateFileSystemOptions() {
                    ChildFiles =
                        new Dictionary<SubstituteFactory.ContentsKey, IList<string>> {
                            {
                                new SubstituteFactory.ContentsKey(@"c:\Temp\.git\refs\heads", "*",
                                    SearchOption.TopDirectoryOnly),
                                new[] { "master" }
                            }, {
                                new SubstituteFactory.ContentsKey(@"c:\Temp\.git\refs\heads\features", "*",
                                    SearchOption.TopDirectoryOnly),
                                new[] { "feature1" }
                            },
                        },
                    ChildDirectories =
                        new Dictionary<SubstituteFactory.ContentsKey, IList<string>> {
                            {
                                new SubstituteFactory.ContentsKey(@"c:\Temp\.git\refs\heads", "*",
                                    SearchOption.TopDirectoryOnly),
                                new[] { @"c:\Temp\.git\refs\heads\features" }
                            }, {
                                new SubstituteFactory.ContentsKey(@"c:\Temp\.git\refs\heads\features", "*",
                                    SearchOption.TopDirectoryOnly),
                                new string[0]
                            },
                        },
                    FileContents =
                        new Dictionary<string, IList<string>> {
                            { @"c:\Temp\.git\HEAD", new[] { "ref: refs/heads/fixes/repository-manager-refresh" } }
                        }
                });

            platform = SubstituteFactory.CreatePlatform();

            repositoryPathConfiguration = new RepositoryPathConfiguration(@"c:\Temp");
            gitConfig = SubstituteFactory.CreateGitConfig();

            repositoryWatcher = SubstituteFactory.CreateRepositoryWatcher();

            gitConfigGetResults = new Dictionary<CreateRepositoryProcessRunnerOptions.GitConfigGetKey, string> {
                {
                    new CreateRepositoryProcessRunnerOptions.GitConfigGetKey("user.email", GitConfigSource.User),
                    "someone@somewhere.com"
                }, {
                    new CreateRepositoryProcessRunnerOptions.GitConfigGetKey("user.name", GitConfigSource.User),
                    "Someone Somewhere"
                }
            };
        }

        [TestFixtureSetUp]
        public void TestFixtureSetUp()
        {
            SubstituteFactory = new SubstituteFactory();
        }

        private readonly CancellationToken cancellationToken = CancellationToken.None;
        private IRepositoryWatcher repositoryWatcher;
        private IPlatform platform;
        private RepositoryPathConfiguration repositoryPathConfiguration;
        private IGitConfig gitConfig;
        private Dictionary<CreateRepositoryProcessRunnerOptions.GitConfigGetKey, string> gitConfigGetResults;

        protected SubstituteFactory SubstituteFactory { get; private set; }

        private RepositoryManager CreateRepositoryManager(IRepositoryProcessRunner repositoryProcessRunner)
        {
            var taskRunner = new TaskRunner(new MainThreadSynchronizationContextBase(), CancellationToken.None);
            taskRunner.Run();

            return new RepositoryManager(platform, taskRunner, gitConfig, repositoryWatcher,
                repositoryProcessRunner, repositoryPathConfiguration, cancellationToken);
        }

        private IRepositoryProcessRunner CreateRepositoryProcessRunner(IList<GitStatus> gitStatusResults = null,
            IList<IList<GitLock>> gitListLocksResults = null)
        {
            return
                SubstituteFactory.CreateRepositoryProcessRunner(new CreateRepositoryProcessRunnerOptions {
                    GitConfigGetResults = gitConfigGetResults,
                    GitStatusResults = gitStatusResults,
                    GitListLocksResults = gitListLocksResults
                });
        }

        [Test]
        public void ShouldBeConstructable()
        {
            var repositoryProcessRunner = CreateRepositoryProcessRunner();
            var repositoryManager = CreateRepositoryManager(repositoryProcessRunner);
            repositoryManager.Should().NotBeNull();
        }

        [Test]
        public void ShouldNotRefreshIfNoGitStatusIsReturned()
        {
            var repositoryProcessRunner = CreateRepositoryProcessRunner(new GitStatus[0], new IList<GitLock>[0]);
            var repositoryManager = CreateRepositoryManager(repositoryProcessRunner);
            repositoryManager.Initialize();
            repositoryManager.Start();

            var repositoryManagerListener = Substitute.For<IRepositoryManagerListener>();
            repositoryManagerListener.AttachListener(repositoryManager);

            repositoryManager.Refresh();

            Thread.Sleep(1000);

            repositoryManagerListener.AssertDidNotReceiveAnyCalls();
        }

        [Test]
        public void ShouldRefreshAndReturnCombinedStatusAndLockInformation1()
        {
            var responseGitStatus = new GitStatus {
                LocalBranch = "master",
                Entries =
                    new List<GitStatusEntry> {
                        new GitStatusEntry("SomeLockedBinary.psd", null, "SomeLockedBinary.psd", GitFileStatus.Modified),
                        new GitStatusEntry("subFolder/AnotherLockedBinary.psd", null, "subFolder/AnotherLockedBinary.psd", GitFileStatus.Modified),
                        new GitStatusEntry("subFolder/UnLockedBinary.psd", null, "subFolder/UnLockedBinary.psd", GitFileStatus.Modified),
                    }
            };

            var responseGitLocks = new GitLock[] {
                new GitLock("SomeLockedBinary.psd", "SomeLockedBinary.psd", "Someone"),
                new GitLock("SomeoneElsesBinary.psd", "SomeoneElsesBinary.psd", "SomeoneElse"),
                new GitLock("subFolder/AnotherLockedBinary.psd", "subFolder/AnotherLockedBinary.psd", "Someone"),
            };

            var expectedGitStatus = new GitStatus {
                LocalBranch = "master",
                Entries =
                    new List<GitStatusEntry> {
                        new GitStatusEntry("SomeLockedBinary.psd", null, "SomeLockedBinary.psd", GitFileStatus.Modified, gitLock: new GitLock("SomeLockedBinary.psd", "SomeLockedBinary.psd", "Someone")),
                        new GitStatusEntry("subFolder/AnotherLockedBinary.psd", null, "subFolder/AnotherLockedBinary.psd", GitFileStatus.Modified, gitLock: new GitLock("subFolder/AnotherLockedBinary.psd", "subFolder/AnotherLockedBinary.psd", "Someone")),
                        new GitStatusEntry("subFolder/UnLockedBinary.psd", null, "subFolder/UnLockedBinary.psd", GitFileStatus.Modified),
                    }
            };

            var repositoryProcessRunner = CreateRepositoryProcessRunner(new[] { responseGitStatus },
                new IList<GitLock>[] { responseGitLocks });

            var repositoryManager = CreateRepositoryManager(repositoryProcessRunner);
            repositoryManager.Initialize();
            repositoryManager.Start();

            var repositoryManagerListener = Substitute.For<IRepositoryManagerListener>();
            repositoryManagerListener.AttachListener(repositoryManager);

            GitStatus? result = null;
            repositoryManager.OnRepositoryChanged += s => { result = s; };

            repositoryManager.Refresh();

            Thread.Sleep(1000);

            repositoryManagerListener.Received(1).OnRepositoryChanged(Args.GitStatus);

            result.HasValue.Should().BeTrue();
            result.Value.AssertEqual(expectedGitStatus);

            repositoryManagerListener.DidNotReceive().OnActiveBranchChanged();
            repositoryManagerListener.DidNotReceive().OnActiveRemoteChanged();
            repositoryManagerListener.DidNotReceive().OnHeadChanged();
            repositoryManagerListener.DidNotReceive().OnLocalBranchListChanged();
            repositoryManagerListener.DidNotReceive().OnRemoteBranchListChanged();
            repositoryManagerListener.DidNotReceive().OnRemoteOrTrackingChanged();
        }

        [Test]
        public void ShouldRefreshAndReturnCombinedStatusAndLockInformation2()
        {
            var responseGitStatus = new GitStatus {
                LocalBranch = "master",
                Entries =
                    new List<GitStatusEntry> {
                        new GitStatusEntry("SomeLockedBinary.psd", null, "SomeLockedBinary.psd", GitFileStatus.Modified),
                        new GitStatusEntry("subFolder/AnotherLockedBinary.psd", null, "subFolder/AnotherLockedBinary.psd", GitFileStatus.Modified),
                        new GitStatusEntry("subFolder/UnLockedBinary.psd", null, "subFolder/UnLockedBinary.psd", GitFileStatus.Modified),
                    }
            };

            var responseGitLocks = new GitLock[] {
                new GitLock("SomeLockedBinary.psd", "SomeLockedBinary.psd", "Someone"),
                new GitLock("SomeoneElsesBinary.psd", "SomeoneElsesBinary.psd", "SomeoneElse"),
                new GitLock("subFolder/AnotherLockedBinary.psd", "subFolder/AnotherLockedBinary.psd", "Someone"),
            };

            var expectedGitStatus = new GitStatus {
                LocalBranch = "master",
                Entries =
                    new List<GitStatusEntry> {
                        new GitStatusEntry("SomeLockedBinary.psd", null, "SomeLockedBinary.psd", GitFileStatus.Modified,
                            gitLock: new GitLock("SomeLockedBinary.psd", "SomeLockedBinary.psd", "Someone")),
                        new GitStatusEntry("subFolder/AnotherLockedBinary.psd", null, "subFolder/AnotherLockedBinary.psd", GitFileStatus.Modified),
                        new GitStatusEntry("subFolder/UnLockedBinary.psd", null, "subFolder/UnLockedBinary.psd", GitFileStatus.Modified
                            
                            //This lock intentionally left missing to catch false positives
                            //,gitLock: new GitLock("subFolder/AnotherLockedBinary.psd", "subFolder/AnotherLockedBinary.psd", "Someone")
                            
                        ),
                    }
            };

            var repositoryProcessRunner = CreateRepositoryProcessRunner(new[] { responseGitStatus },
                new IList<GitLock>[] { responseGitLocks });

            var repositoryManager = CreateRepositoryManager(repositoryProcessRunner);
            repositoryManager.Initialize();
            repositoryManager.Start();

            var repositoryManagerListener = Substitute.For<IRepositoryManagerListener>();
            repositoryManagerListener.AttachListener(repositoryManager);

            GitStatus? result = null;
            repositoryManager.OnRepositoryChanged += s => { result = s; };

            repositoryManager.Refresh();

            Thread.Sleep(1000);

            repositoryManagerListener.Received(1).OnRepositoryChanged(Args.GitStatus);

            result.HasValue.Should().BeTrue();
            result.Value.AssertNotEqual(expectedGitStatus);

            repositoryManagerListener.DidNotReceive().OnActiveBranchChanged();
            repositoryManagerListener.DidNotReceive().OnActiveRemoteChanged();
            repositoryManagerListener.DidNotReceive().OnHeadChanged();
            repositoryManagerListener.DidNotReceive().OnLocalBranchListChanged();
            repositoryManagerListener.DidNotReceive().OnRemoteBranchListChanged();
            repositoryManagerListener.DidNotReceive().OnRemoteOrTrackingChanged();
        }

        [Test]
        public void ShouldRefreshAndReturnWithEmptyGitLockResponse()
        {
            var responseGitStatus = new GitStatus {
                LocalBranch = "master",
                Entries =
                    new List<GitStatusEntry> {
                        new GitStatusEntry("Some.sln", null, "Some.sln", GitFileStatus.Modified)
                    }
            };

            var repositoryProcessRunner = CreateRepositoryProcessRunner(new[] { responseGitStatus },
                new IList<GitLock>[] { new GitLock[0] });

            var repositoryManager = CreateRepositoryManager(repositoryProcessRunner);
            repositoryManager.Initialize();
            repositoryManager.Start();

            var repositoryManagerListener = Substitute.For<IRepositoryManagerListener>();
            repositoryManagerListener.AttachListener(repositoryManager);

            GitStatus? result = null;
            repositoryManager.OnRepositoryChanged += s => { result = s; };

            repositoryManager.Refresh();

            Thread.Sleep(1000);

            repositoryManagerListener.Received(1).OnRepositoryChanged(Args.GitStatus);

            result.HasValue.Should().BeTrue();
            result.Value.AssertEqual(responseGitStatus);

            repositoryManagerListener.DidNotReceive().OnActiveBranchChanged();
            repositoryManagerListener.DidNotReceive().OnActiveRemoteChanged();
            repositoryManagerListener.DidNotReceive().OnHeadChanged();
            repositoryManagerListener.DidNotReceive().OnLocalBranchListChanged();
            repositoryManagerListener.DidNotReceive().OnRemoteBranchListChanged();
            repositoryManagerListener.DidNotReceive().OnRemoteOrTrackingChanged();
        }

        [Test]
        public void ShouldRefreshAndReturnWithNoGitLockResponse()
        {
            var responseGitStatus = new GitStatus {
                LocalBranch = "master",
                Entries =
                    new List<GitStatusEntry> {
                        new GitStatusEntry("Some.sln", null, "Some.sln", GitFileStatus.Modified)
                    }
            };

            var repositoryProcessRunner = CreateRepositoryProcessRunner(new[] { responseGitStatus },
                new IList<GitLock>[0]);

            var repositoryManager = CreateRepositoryManager(repositoryProcessRunner);
            repositoryManager.Initialize();
            repositoryManager.Start();

            var repositoryManagerListener = Substitute.For<IRepositoryManagerListener>();
            repositoryManagerListener.AttachListener(repositoryManager);

            GitStatus? result = null;
            repositoryManager.OnRepositoryChanged += s => { result = s; };

            repositoryManager.Refresh();

            Thread.Sleep(1000);

            repositoryManagerListener.Received(1).OnRepositoryChanged(Args.GitStatus);

            result.HasValue.Should().BeTrue();
            result.Value.AssertEqual(responseGitStatus);

            repositoryManagerListener.DidNotReceive().OnActiveBranchChanged();
            repositoryManagerListener.DidNotReceive().OnActiveRemoteChanged();
            repositoryManagerListener.DidNotReceive().OnHeadChanged();
            repositoryManagerListener.DidNotReceive().OnLocalBranchListChanged();
            repositoryManagerListener.DidNotReceive().OnRemoteBranchListChanged();
            repositoryManagerListener.DidNotReceive().OnRemoteOrTrackingChanged();
        }
    }
}