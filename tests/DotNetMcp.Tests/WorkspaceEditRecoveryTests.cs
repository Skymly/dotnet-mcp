using System.Diagnostics;
using System.Text;
using DotNetMcp.Server;
using ModelContextProtocol.Client;

namespace DotNetMcp.Tests;

public class WorkspaceEditRecoveryTests
{
    [Theory]
    [InlineData(1, false, "restored")]
    [InlineData(1, true, "conflict")]
    [InlineData(2, false, "completed")]
    public async Task stdio_restart_after_killing_apply_preserves_exact_bytes(
        int pauseAfter, bool editAfterCrash, string expectedState)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var ct = timeout.Token;
        var temp = Directory.CreateTempSubdirectory("dnmcp-recovery-");
        var source = PathPolicy.Normalize(temp.CreateSubdirectory("src").FullName);
        var state = PathPolicy.Normalize(Path.Combine(temp.FullName, "state"));
        var project = Path.Combine(source, "Demo.csproj");
        await File.WriteAllTextAsync(project, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>""", ct);
        var declaration = Path.Combine(source, "Service.cs");
        var consumer = Path.Combine(source, "Use.cs");
        const string declarationText = "public static class Service { public static int Before() => 1; }\r\n";
        await File.WriteAllTextAsync(declaration, declarationText, new UTF8Encoding(true), ct);
        await File.WriteAllTextAsync(consumer, "class Use { int Read() => Service.Before(); }\n", Encoding.Unicode, ct);
        var original = new Dictionary<string, byte[]>(PathPolicy.Comparer)
        {
            [declaration] = await File.ReadAllBytesAsync(declaration, ct),
            [consumer] = await File.ReadAllBytesAsync(consumer, ct)
        };
        var intended = original.ToDictionary(pair => pair.Key, pair =>
        {
            Assert.True(FileTextCodec.TryReadLossless(pair.Value, out var text, out var encoding));
            return (byte[])[.. encoding.Preamble, .. encoding.GetBytes(text.Replace("Before", "After", StringComparison.Ordinal))];
        }, PathPolicy.Comparer);
        try
        {
            var paused = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using (var client = await ConnectAsync(source, state, pauseAfter, line =>
            {
                const string prefix = "recovery-probe-paused ";
                if (line.StartsWith(prefix, StringComparison.Ordinal))
                    paused.TrySetResult(int.Parse(line[prefix.Length..], System.Globalization.CultureInfo.InvariantCulture));
            }, ct))
            {
                await CallAsync<WorkspaceOpenResultDto>(client, "workspace_open", new() { ["path"] = project }, ct);
                while (true)
                {
                    var status = await CallAsync<WorkspaceStatusDto>(client, "workspace_status", [], ct);
                    if (status.Phase == "ready")
                        break;
                    Assert.Equal("loading", status.Phase);
                    await Task.Delay(75, ct);
                }
                var symbol = await CallAsync<SymbolResolveAtResultDto>(client, "symbol_resolve_at", new()
                {
                    ["path"] = declaration,
                    ["line"] = 1,
                    ["column"] = declarationText.IndexOf("Before", StringComparison.Ordinal) + 1
                }, ct);
                var preview = await CallAsync<SymbolPreviewRenameResultDto>(client, "symbol_preview_rename", new()
                {
                    ["handle"] = symbol.Handle,
                    ["newName"] = "After"
                }, ct);
                Assert.NotNull(preview.Documents);
                Assert.Equal(2, preview.Documents.Count);
                var apply = CallAsync<SymbolApplyRenameResultDto>(client, "symbol_apply_rename", new() { ["previewId"] = preview.PreviewId }, ct);
                if (await Task.WhenAny(apply, paused.Task).WaitAsync(ct) == apply)
                {
                    await apply;
                    Assert.Fail("The process returned from apply before the write barrier.");
                }
                using var child = Process.GetProcessById(await paused.Task.WaitAsync(ct));
                child.Kill(entireProcessTree: true);
                await child.WaitForExitAsync(ct);
                await Assert.ThrowsAnyAsync<Exception>(() => apply.WaitAsync(ct));
            }

            var applied = original.Keys.Where(p => File.ReadAllBytes(p).SequenceEqual(intended[p])).ToArray();
            Assert.Equal(pauseAfter, applied.Length);
            var untouched = original.Keys.Except(applied, PathPolicy.Comparer).ToArray();
            Assert.All(untouched, p => Assert.Equal(original[p], File.ReadAllBytes(p)));
            byte[] userBytes = Encoding.UTF8.GetBytes("// User edit after process termination.\r\n");
            if (editAfterCrash)
                await File.WriteAllBytesAsync(untouched.Single(), userBytes, ct);

            for (var restart = 0; restart < 2; restart++)
            {
                await using var restarted = await ConnectAsync(source, state, 0, null, ct);
                var status = await CallAsync<WorkspaceStatusDto>(restarted, "workspace_status", [], ct);
                Assert.Equal("idle", status.Phase); // Recovery already ran before MCP initialize.
                if (restart == 0 || editAfterCrash)
                {
                    var result = Assert.Single(status.Recovery!);
                    Assert.Equal(expectedState, result.State);
                    Assert.Equal(project, result.Workspace, ignoreCase: OperatingSystem.IsWindows());
                    if (editAfterCrash)
                    {
                        Assert.Equal(untouched.Single(), Assert.Single(result.ConflictingPaths), ignoreCase: OperatingSystem.IsWindows());
                        Assert.Equal(userBytes, File.ReadAllBytes(untouched.Single()));
                        Assert.Equal(intended[applied.Single()], File.ReadAllBytes(applied.Single()));
                    }
                }
                else
                    Assert.Null(status.Recovery);
                if (!editAfterCrash)
                {
                    var expected = expectedState == "completed" ? intended : original;
                    Assert.All(expected, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
                }
            }
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void recovery_rechecks_current_roots_and_redirected_targets_before_mutating(bool redirect)
    {
        var temp = Directory.CreateTempSubdirectory("dnmcp-authority-");
        var root = PathPolicy.Normalize(temp.CreateSubdirectory("project").FullName);
        var linked = PathPolicy.Normalize(temp.CreateSubdirectory("linked").FullName);
        var state = PathPolicy.Normalize(Path.Combine(temp.FullName, "state"));
        var workspace = Path.Combine(root, "App.csproj");
        var a = Path.Combine(root, "First.cs");
        var b = Path.Combine(linked, "Second.cs");
        var other = Path.Combine(root, "Other.cs");
        byte[] original = [0x61];
        byte[] intended = [0x62];
        try
        {
            File.WriteAllBytes(a, original);
            File.WriteAllBytes(b, original);
            File.WriteAllBytes(other, original);
            var full = new WorkspaceEditJournal(TrustedRoots.Create([root, linked]), state);
            var entries = new[] { WorkspaceEditJournal.Entry.From(a, original, intended),
                                  WorkspaceEditJournal.Entry.From(b, original, intended) };
            using (full.Enter())
            {
                full.Begin(workspace, entries);
                full.ReplaceVerified(a, entries[0].OriginalHash, intended);
            }
            var restarting = redirect ? full : new WorkspaceEditJournal(TrustedRoots.Create([root]), state);
            if (redirect)
            {
                File.Delete(b);
                File.CreateSymbolicLink(b, other);
            }
            var conflict = Assert.Single(restarting.Recover());
            Assert.Equal("conflict", conflict.State);
            Assert.Empty(conflict.ConflictingPaths);
            Assert.Equal(intended, File.ReadAllBytes(a));
            Assert.Equal(original, File.ReadAllBytes(other));
            using (restarting.Enter())
            {
                Assert.Throws<IOException>(() => restarting.Begin(Path.Combine(root, "Other.csproj"), [entries[0]]));
            }
            if (redirect)
            {
                File.Delete(b);
                File.WriteAllBytes(b, original);
            }
            Assert.Equal("restored", Assert.Single(full.Recover()).State);
            Assert.Equal(original, File.ReadAllBytes(a));
            Assert.Empty(full.Recover());
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    private static async Task<T> CallAsync<T>(McpClient client, string name, Dictionary<string, object?> args, CancellationToken ct)
    {
        var response = await client.CallToolAsync(name, args, cancellationToken: ct);
        Assert.True(response.IsError is not true, InProcessMcpFixture.TextOf(response));
        return InProcessMcpFixture.Deserialize<T>(response);
    }

    private static async Task<McpClient> ConnectAsync(
        string root, string state, int pauseAfter, Action<string>? stderr, CancellationToken ct) =>
        await McpClient.CreateAsync(new StdioClientTransport(new()
        {
            Name = "recovery-probe",
            Command = "dotnet",
            Arguments = [Path.Combine(AppContext.BaseDirectory, "DotNetMcp.RecoveryProbe.dll"), state,
                pauseAfter.ToString(System.Globalization.CultureInfo.InvariantCulture), "--roots", root],
            StandardErrorLines = stderr,
            EnvironmentVariables = new Dictionary<string, string?> { ["DOTNET_MCP_TRUSTED_ROOTS"] = null }
        }), cancellationToken: ct);
}
