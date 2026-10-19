using System.Windows;

// テストから内部処理を直接検証できるようにする(アプリ公開APIを広げないため)
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("TwinCord.Tests")]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
