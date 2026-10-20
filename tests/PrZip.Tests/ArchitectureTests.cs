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

using System.Xml.Linq;
using PrZip.Tests.Infrastructure;
using Xunit;

namespace PrZip.Tests;

public class ArchitectureTests
{
    [Requires7zFact]
    public void Official7z_IsDiscoveredSuccessfully()
    {
        Assert.True(Official7zFinder.IsAvailable, 
            "El binario oficial de 7-Zip debe ser descubierto para las pruebas de interoperabilidad.");
        Assert.NotNull(Official7zFinder.ExecutablePath);
        Assert.True(File.Exists(Official7zFinder.ExecutablePath));
        Assert.NotNull(Official7zFinder.Version);
    }

    [Fact]
    public void RuntimeProjects_HaveZeroPackageReferences()
    {
        // Buscar el directorio raíz de la solución
        string currentDir = AppContext.BaseDirectory;
        DirectoryInfo? dir = new DirectoryInfo(currentDir);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "PrZip.slnx")) && !File.Exists(Path.Combine(dir.FullName, "PrZip.sln")))
        {
            dir = dir.Parent;
        }

        Assert.NotNull(dir);
        string srcDir = Path.Combine(dir.FullName, "src");
        Assert.True(Directory.Exists(srcDir), "El directorio /src/ debe existir.");

        string[] projectFiles = Directory.GetFiles(srcDir, "*.csproj", SearchOption.AllDirectories);
        Assert.NotEmpty(projectFiles);

        foreach (string projectFile in projectFiles)
        {
            XDocument doc = XDocument.Load(projectFile);
            var packageRefs = doc.Descendants("PackageReference").ToList();

            Assert.True(packageRefs.Count == 0,
                $"El proyecto de runtime '{Path.GetFileName(projectFile)}' viola la regla de cero dependencias externas. " +
                $"Contiene {packageRefs.Count} referencias PackageReference.");
        }
    }

    [Fact]
    public void Core_DoesNotReferenceTheRarAssembly()
    {
        // El código con la licencia de unrar vive solo en PrZip.Rar, que depende del núcleo y no al revés:
        // quien usa 7z, TAR, ISO o XZ no lleva código con otra licencia.
        System.Reflection.Assembly[] core =
        [
            typeof(Archive7z).Assembly,
            typeof(PrZip.Container.Headers.Folder).Assembly,
            typeof(PrZip.Codecs.Lzma.LzmaDecoder).Assembly,
            typeof(PrZip.Exceptions.PrZipException).Assembly,
        ];

        foreach (var assembly in core)
        {
            Assert.DoesNotContain("PrZip.Rar", assembly.GetReferencedAssemblies().Select(a => a.Name));
        }
    }
}
