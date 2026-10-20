// =============================================================================
//  PrZip — Patagonia Robot Archive Engine
//  Lee y escribe 7z; abre RAR, TAR, GZip, BZip2, XZ e ISO 9660.
// =============================================================================
//
//  Copyright (c) 2026 Claudio Andino
//  Developed under the Patagonia Robot initiative
//
//  Licensed under the Apache License, Version 2.0 (the "License");
//  you may not use this file except in compliance with the License.
//  You may obtain a copy of the License at:
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//
// =============================================================================

using Xunit;

namespace PrZip.Tests;

/// <summary>
/// Cada archivo de código dice bajo qué licencia está, y el código con licencia ajena queda donde corresponde:
/// lo de unrar solo en PrZip.Rar, la tabla de bzip2 solo en su archivo. El motor puede terminar en un producto
/// cerrado: esto evita que un archivo sin encabezado o un port fuera de lugar pase sin que nadie lo vea.
/// </summary>
public class LicenseHeaderTests
{
    private const string UnrarParagraph = "UnRAR source code may be used in any software to handle RAR archives";
    private const string BZip2Notice = "copyright (C) 1996-2019 Julian R Seward";
    private const string Apache = "Licensed under the Apache License, Version 2.0";

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PrZip.slnx")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        return dir.FullName;
    }

    private static readonly string[] SourceFolders = ["src", "tools", "tests"];

    private static IEnumerable<string> SourceFiles(string root) =>
        SourceFolders
            .SelectMany(d => Directory.EnumerateFiles(Path.Combine(root, d), "*.cs", SearchOption.AllDirectories))
            .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                        && !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal));

    [Fact]
    public void EverySourceFile_DeclaresItsLicense_AndForeignCodeStaysInPlace()
    {
        string root = RepoRoot();
        string rarDir = Path.Combine(root, "src", "PrZip.Rar") + Path.DirectorySeparatorChar;
        var problems = new List<string>();

        foreach (string file in SourceFiles(root))
        {
            string head = string.Concat(File.ReadLines(file).Take(30).Select(l => l + "\n"));
            string relative = Path.GetRelativePath(root, file);
            bool apache = head.Contains(Apache, StringComparison.Ordinal);
            bool unrar = head.Contains(UnrarParagraph, StringComparison.Ordinal);
            bool bzip2 = head.Contains(BZip2Notice, StringComparison.Ordinal);

            if (!apache && !unrar && !bzip2)
            {
                problems.Add($"{relative}: sin encabezado de licencia");
            }

            if (unrar && !file.StartsWith(rarDir, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add($"{relative}: código de unrar fuera de PrZip.Rar");
            }

            if (bzip2 && Path.GetFileName(file) != "BZip2RandomTable.cs")
            {
                problems.Add($"{relative}: aviso de bzip2 fuera de BZip2RandomTable.cs");
            }
        }

        Assert.Empty(problems);
    }

    [Fact]
    public void LicenseAndNotice_AreAtTheRoot()
    {
        string root = RepoRoot();
        Assert.Contains("TERMS AND CONDITIONS FOR USE, REPRODUCTION, AND DISTRIBUTION", File.ReadAllText(Path.Combine(root, "LICENSE")));
        string notice = File.ReadAllText(Path.Combine(root, "NOTICE"));
        Assert.Contains(UnrarParagraph, notice);
        Assert.Contains("Julian R Seward", notice);
        Assert.Contains("LZMA SDK", notice);
    }
}
