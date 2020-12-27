using System.Collections.Generic;
using System.IO;
using UnityEditor;

[InitializeOnLoad]
public class UnityPackageGenerator
{
    static UnityPackageGenerator()
    {
        UnityPackage();
    }


    [MenuItem("Window/ZuKit/Generate UnityPackage")]
    public static void UnityPackage()
    {
        var assetPaths = new List<string>();

        var path = "Assets/ZuKit";
        CollectPathRecursive(path, assetPaths);

        AssetDatabase.ExportPackage(assetPaths.ToArray(), "ZuKit.unitypackage", ExportPackageOptions.IncludeDependencies);
    }

    private static void CollectPathRecursive(string path, List<string> collectedPaths)
    {
        var filePaths = Directory.GetFiles(path);
        foreach (var filePath in filePaths)
        {
            collectedPaths.Add(filePath);
        }

        var modulePaths = Directory.GetDirectories(path);
        foreach (var folderPath in modulePaths)
        {
            CollectPathRecursive(folderPath, collectedPaths);
        }
    }
}