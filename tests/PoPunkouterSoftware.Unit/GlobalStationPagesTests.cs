using PoPunkouterSoftware.Client.Features.Global;
using PoPunkouterSoftware.Infrastructure;
using PoPunkouterSoftware.Shared;

namespace PoPunkouterSoftware.Unit;

/// <summary>
/// The station pages ported from GLOBAL: the briefing's per-repo commits and brain files, the
/// lessons' sources, the Disk page's cleanup commands and the Lessons page's inline markdown.
/// </summary>
public sealed class GlobalStationPagesTests
{
    private static string SnapshotDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "PoPunkouterSoftware.slnx")))
            dir = dir.Parent;

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("Repo root not found"),
            "src", "PoPunkouterSoftware.Api", "App_Data", "global");
    }

    // ─── Briefing ────────────────────────────────────────────────────────────

    [Fact]
    public void Briefing_OnTheCommittedSnapshot_ListsCommitsAndBrainsOfThePeriod()
    {
        var briefing = new GlobalDataStore(SnapshotDir()).LoadBriefing();

        briefing.Since.Should().NotBeNull();
        foreach (var repo in briefing.Repos)
        {
            repo.CommitList.Should().OnlyContain(c => c.Iso > briefing.Since, $"{repo.Repo} lists only the period's commits");
            repo.BrainFiles.Should().HaveCount(repo.NewBrains, $"{repo.Repo} names every new brain");
        }

        briefing.Repos.Where(r => r.CommitList.Count > 0).Should().OnlyContain(r => !string.IsNullOrEmpty(r.GitHubSlug));
    }

    [Fact]
    public void Briefing_ComparesTwoCrawls_CommitsBrainsAndACiOnlyFlip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "po-global-briefing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "history"));
        try
        {
            File.WriteAllText(Path.Combine(dir, "fleet.json"), """
                {
                  "generated": "2026-11-27T07:00:00+00:00",
                  "repos": {
                    "PoA": {
                      "name": "PoA",
                      "git": {
                        "is_git": true, "commits_total": 12, "remote": "https://github.com/owner/PoA.git",
                        "recent_commits": [
                          { "iso": "2026-11-27T06:00:00+00:00", "hash": "aaa1111", "msg": "new one" },
                          { "iso": "2026-11-26T05:00:00+00:00", "hash": "bbb2222", "msg": "before the period" }
                        ]
                      },
                      "ci": { "latest_run": { "conclusion": "success" } },
                      "onnx": [
                        { "rel": "PoA/models/new.onnx", "name": "new.onnx", "sha1": "new000000000", "mtime": 1 },
                        { "rel": "PoA/models/old.onnx", "name": "old.onnx", "sha1": "old000000000", "mtime": 1 }
                      ]
                    },
                    "PoB": { "name": "PoB", "git": { "is_git": true, "commits_total": 3 }, "ci": { "latest_run": { "conclusion": "failure" } } }
                  }
                }
                """);
            File.WriteAllLines(Path.Combine(dir, "history", "station_timeline.jsonl"),
            [
                """{"when": "2026-11-26T07:00:00+00:00", "repos": {"PoA": {"commits_total": 10, "onnx_shas": ["old000000000"], "ci": "success"}, "PoB": {"commits_total": 3, "ci": "success"}}}""",
                """{"when": "2026-11-27T07:00:00+00:00", "repos": {"PoA": {"commits_total": 12, "onnx_shas": ["old000000000", "new000000000"], "ci": "success"}, "PoB": {"commits_total": 3, "ci": "failure"}}}""",
            ]);

            var briefing = new GlobalDataStore(dir).LoadBriefing();

            briefing.IsBaseline.Should().BeFalse();
            var a = briefing.Repos.Single(r => r.Repo == "PoA");
            a.Commits.Should().Be(2);
            a.CommitList.Select(c => c.Hash).Should().Equal("aaa1111");
            a.BrainFiles.Select(b => b.Name).Should().Equal("new.onnx");
            a.GitHubSlug.Should().Be("owner/PoA");

            var b = briefing.Repos.Should().ContainSingle(r => r.Repo == "PoB", "a CI flip alone is a change worth listing").Subject;
            b.CiBefore.Should().Be("success");
            b.CiAfter.Should().Be("failure");
            briefing.Items.Should().Contain(i => i.Repo == "PoB" && i.Kind == "ci");
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    // ─── Lessons ─────────────────────────────────────────────────────────────

    [Fact]
    public void Lessons_CarryEveryPlaceTheyWereFound()
    {
        var lessons = new GlobalDataStore(SnapshotDir()).LoadLessons();

        lessons.Should().Contain(l => l.Sources.Count > 1, "a bullet found in several repos keeps each source");
        lessons.Where(l => !l.Manual).Should().OnlyContain(l => l.Sources.Count > 0 && l.Sources.All(s => !string.IsNullOrEmpty(s.File)));
    }

    [Theory]
    [InlineData("Run `uv sync` first", "Run <code>uv sync</code> first")]
    [InlineData("**Never** push", "<strong>Never</strong> push")]
    [InlineData("See [the docs](https://example.invalid/x)", "See the docs")]
    [InlineData("<script>alert(1)</script> `<b>`", "&lt;script&gt;alert(1)&lt;/script&gt; <code>&lt;b&gt;</code>")]
    public void Lessons_InlineMarkdown_IsEscapedFirst(string text, string expected) =>
        GlobalLessons.Inline(text).Should().Be(expected);

    // ─── Disk ────────────────────────────────────────────────────────────────

    [Fact]
    public void Disk_CleanupCommands_DeleteOnlyWhatIsSafe()
    {
        static GlobalHeavyDirDto Dir(string safety, string category = "x") =>
            new() { Rel = "PoA/sub/folder", Safety = safety, Category = category };

        GlobalDisk.CleanupCommand(Dir("regenerable"), @"C:\ws")
            .Should().Be("Remove-Item -LiteralPath \"C:\\ws\\PoA\\sub\\folder\" -Recurse -Force");
        GlobalDisk.CleanupCommand(Dir("recreate"), @"C:\ws\")
            .Should().EndWith("Set-Location \"C:\\ws\\PoA\\sub\"; uv sync");
        GlobalDisk.CleanupCommand(Dir("never"), null)
            .Should().Be("git -C \"PoA\\sub\" gc --prune=now", "without the helper the path is relative to the workspace");

        var review = GlobalDisk.CleanupCommand(Dir("review", "training_output"), @"C:\ws");
        review.Should().Contain("-WhatIf").And.NotContain("-Force");
        GlobalDisk.CleanupCommand(Dir("review", "builds"), @"C:\ws")
            .Should().StartWith("Get-ChildItem").And.NotContain("Remove-Item");
    }
}
