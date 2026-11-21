param(
  [Parameter(Mandatory)]
  [string]$PackageDirectory,

  [Parameter(Mandatory)]
  [string]$ExpectedServerName
)

$ErrorActionPreference = 'Stop'

function Read-PackageFile([System.IO.Compression.ZipArchive]$Archive, [string]$Path) {
  $entry = $Archive.GetEntry($Path)
  if ($null -eq $entry) {
    throw "Missing '$Path' in the NuGet package"
  }

  $reader = [System.IO.StreamReader]::new($entry.Open())
  try {
    $reader.ReadToEnd()
  } finally {
    $reader.Dispose()
  }
}

$packages = @(Get-ChildItem -LiteralPath $PackageDirectory -Filter '*.nupkg' -File)
if ($packages.Count -ne 1 -or $packages[0].Name -notlike 'Skymly.DotNetMcp.*') {
  throw "Expected one Skymly.DotNetMcp package in '$PackageDirectory'"
}

$archive = [System.IO.Compression.ZipFile]::OpenRead($packages[0].FullName)
try {
  $nuspec = [xml](Read-PackageFile $archive 'Skymly.DotNetMcp.nuspec')
  $metadata = $nuspec.package.metadata
  if ($metadata.id -ne 'Skymly.DotNetMcp') {
    throw 'Expected PackageId Skymly.DotNetMcp in the nuspec'
  }
  if ($metadata.packageTypes.packageType.name -notcontains 'McpServer') {
    throw 'Expected PackageType McpServer in the nuspec'
  }

  $manifest = Read-PackageFile $archive '.mcp/server.json' | ConvertFrom-Json
  if ($manifest.name -cne $ExpectedServerName) {
    throw "Embedded MCP server name '$($manifest.name)' must exactly match '$ExpectedServerName'"
  }

  $readmePath = [string]$metadata.readme
  if ([string]::IsNullOrWhiteSpace($readmePath)) {
    throw 'The nuspec must declare the package README for MCP Registry ownership'
  }

  $readme = Read-PackageFile $archive $readmePath.Replace('\', '/')
  # The Registry requires a case-sensitive ownership token with a name boundary.
  $ownershipToken = [regex]::Escape("mcp-name: $ExpectedServerName") + '(?=$|[^A-Za-z0-9._/-]|--!?>)'
  if ($readme -cnotmatch $ownershipToken) {
    throw "Package README '$readmePath' must contain the registry ownership token 'mcp-name: $ExpectedServerName'"
  }
} finally {
  $archive.Dispose()
}
