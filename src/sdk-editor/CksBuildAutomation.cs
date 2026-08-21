using System;
using System.IO;
using PugMod;
using UnityEditor;
using UnityEngine;

/// <summary>
/// Entry point for building and installing the mod from batch mode.
///
/// This file is a helper that belongs to the ModSDK project rather than to the mod itself.
/// It is invoked from scripts/build-mod.ps1 through <c>-executeMethod</c>.
/// Placed inside the mod folder it would end up in the build output and reference UnityEditor,
/// which does not exist at run time, so it must live under Assets/Editor/.
/// </summary>
public static class CksBuildAutomation
{
    private const string ModName = "CustomPlayerSkin";
    private const string SettingsPath = "Assets/" + ModName + ".asset";

    /// <summary>Name of the command line argument that carries the install path.</summary>
    private const string InstallPathArg = "-cksInstallPath";

    /// <summary>
    /// Builds the mod and installs it into &lt;ModName&gt;/ under the given directory.
    /// Success is reported through the exit code, where 0 means success.
    /// </summary>
    public static void BuildAndInstall()
    {
        int exitCode = 1;

        try
        {
            string installPath = GetArgument(InstallPathArg);
            if (string.IsNullOrEmpty(installPath))
            {
                Debug.LogError($"[cks] {InstallPathArg} が指定されていない。");
                return;
            }

            var settings = AssetDatabase.LoadAssetAtPath<ModBuilderSettings>(SettingsPath);
            if (settings == null)
            {
                Debug.LogError($"[cks] ビルド設定が見つからない: {SettingsPath}");
                return;
            }

            if (!Directory.Exists(settings.modPath))
            {
                Debug.LogError($"[cks] MOD フォルダが見つからない: {settings.modPath}");
                return;
            }

            Directory.CreateDirectory(installPath);

            Debug.Log($"[cks] ビルド開始: {settings.metadata.name} -> {installPath}");

            // BuildMod invokes its callback synchronously, so the result can be read straight back
            bool succeeded = false;
            ModBuilder.BuildMod(settings, installPath, success => succeeded = success, installInSubDirectory: true);

            if (succeeded)
            {
                string installed = Path.Combine(installPath, settings.metadata.name);
                Debug.Log($"[cks] ビルド成功: {installed}");
                exitCode = 0;
            }
            else
            {
                Debug.LogError("[cks] ビルド失敗。上のログにエラーが出ているはず。");
            }
        }
        catch (Exception ex)
        {
            Debug.LogError($"[cks] ビルド中に例外: {ex}");
        }
        finally
        {
            EditorApplication.Exit(exitCode);
        }
    }

    /// <summary>Reads a single value from the command line Unity was started with.</summary>
    private static string GetArgument(string name)
    {
        string[] args = Environment.GetCommandLineArgs();
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
            {
                return args[i + 1];
            }
        }

        return null;
    }
}
