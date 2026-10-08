using System.Diagnostics;
using System.Reflection;
using System.Xml.Linq;

namespace DotNetMcp.Server;

internal static class FSharpProjectFile
{
    public static IReadOnlyList<string> ReadCompilePaths(string fsprojPath)
    {
        if (!File.Exists(fsprojPath))
        {
            return [];
        }

        XDocument document;
        try
        {
            document = XDocument.Load(fsprojPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return [];
        }

        var dir = Path.GetDirectoryName(Path.GetFullPath(fsprojPath)) ?? "";
        var paths = new List<string>();
        foreach (var include in document.Descendants().Where(e => e.Name.LocalName == "Compile"))
        {
            var spec = (string?)include.Attribute("Include");
            if (string.IsNullOrWhiteSpace(spec))
            {
                continue;
            }

            var combined = Path.GetFullPath(Path.Combine(dir, spec.Replace('\\', Path.DirectorySeparatorChar)));
            var ext = Path.GetExtension(combined);
            if (ext.Equals(".fs", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".fsi", StringComparison.OrdinalIgnoreCase) ||
                ext.Equals(".fsx", StringComparison.OrdinalIgnoreCase))
            {
                paths.Add(combined);
            }
        }

        return paths;
    }

    public static IReadOnlyList<string> ReadDefines(string fsprojPath)
    {
        if (!File.Exists(fsprojPath))
        {
            return [];
        }

        var evaluated = TryEvaluateDefines(fsprojPath);
        if (evaluated is not null)
        {
            return evaluated;
        }

        return ReadDefinesFromXml(fsprojPath);
    }

    internal static IReadOnlyList<string> SplitDefines(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return [];
        }

        return raw.Split(new[] { ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.IndexOf('$') < 0)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadDefinesFromXml(string fsprojPath)
    {
        try
        {
            var document = XDocument.Load(fsprojPath);
            var raw = document.Descendants()
                .Where(e => e.Name.LocalName == "DefineConstants")
                .Select(e => e.Value)
                .LastOrDefault();
            return SplitDefines(raw);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Xml.XmlException)
        {
            return [];
        }
    }

    private static IReadOnlyList<string>? TryEvaluateDefines(string fsprojPath)
    {
        var inProcess = TryEvaluateDefinesInProcess(fsprojPath);
        return inProcess ?? TryEvaluateDefinesWithDotnet(fsprojPath);
    }

    private static IReadOnlyList<string>? TryEvaluateDefinesInProcess(string fsprojPath)
    {
        try
        {
            MsBuildBootstrap.EnsureRegistered();
            var asm = AppDomain.CurrentDomain.GetAssemblies()
                .FirstOrDefault(a => string.Equals(a.GetName().Name, "Microsoft.Build", StringComparison.Ordinal));
            if (asm is null)
            {
                var sdk = MsBuildBootstrap.TryFindNewestSdkDirectory();
                if (sdk is null)
                {
                    return null;
                }

                var dll = Path.Combine(sdk, "Microsoft.Build.dll");
                if (!File.Exists(dll))
                {
                    return null;
                }

                asm = Assembly.LoadFrom(dll);
            }

            var collectionType = asm.GetType("Microsoft.Build.Evaluation.ProjectCollection");
            var projectType = asm.GetType("Microsoft.Build.Evaluation.Project");
            if (collectionType is null || projectType is null)
            {
                return null;
            }

            var collection = Activator.CreateInstance(collectionType);
            if (collection is null)
            {
                return null;
            }

            try
            {
                var ctor = projectType.GetConstructors().FirstOrDefault(c =>
                {
                    var parameters = c.GetParameters();
                    return parameters.Length == 4 && parameters[0].ParameterType == typeof(string);
                });
                if (ctor is null)
                {
                    return null;
                }

                var project = ctor.Invoke(new object?[] { fsprojPath, null, null, collection });
                var getPropertyValue = projectType.GetMethod("GetPropertyValue", new[] { typeof(string) });
                var raw = getPropertyValue?.Invoke(project, new object[] { "DefineConstants" }) as string;
                return raw is null ? null : SplitDefines(raw);
            }
            finally
            {
                if (collection is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }
        }
        catch (Exception ex) when (ex is IOException
            or UnauthorizedAccessException
            or InvalidOperationException
            or TargetInvocationException
            or FileNotFoundException
            or BadImageFormatException)
        {
            return null;
        }
    }

    private static IReadOnlyList<string>? TryEvaluateDefinesWithDotnet(string fsprojPath)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "dotnet",
                WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(fsprojPath)) ?? "",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("msbuild");
            psi.ArgumentList.Add(fsprojPath);
            psi.ArgumentList.Add("-getProperty:DefineConstants");
            psi.ArgumentList.Add("-nologo");
            psi.ArgumentList.Add("-verbosity:quiet");
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(20_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return null;
            }

            process.WaitForExit();
            _ = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0)
            {
                return null;
            }

            return SplitDefines(stdoutTask.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
