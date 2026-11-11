////////////////////////////////////////////////////////////////////////////////////////
// This file is part of the U3 SDK: https://github.com/smartlydressedgames/u3-sdk/    //
// Please refer to the included LICENSE.txt for copyright notice and license details. //
////////////////////////////////////////////////////////////////////////////////////////
using System.IO;
using System.IO.Compression;
using UnityEditor;
using Unturned.SteamCmd;
using Unturned.UnityEx;

/// <summary>
/// Build and package the mod for sharing with friends. Unlike "Build Standalone Platforms" (Smartly Dressed Games'
/// release pipeline) this only needs the Windows build support that ships with the editor.
/// </summary>
public partial class BuildMethods
{
	/// <summary>
	/// Files from Builds/Shared that belong in a build. The rest of that folder (Cloud, Worlds, Logs,
	/// Preferences.json) is the editor's own save data and settings, which shouldn't be handed out.
	/// </summary>
	private static readonly string[] SHARED_FILES_TO_PACKAGE = { "ModInfo.json", "Status.json" };

	/// <summary>
	/// Files from the project root the U3 SDK License Agreement requires including with any distributed mod.
	/// </summary>
	private static readonly string[] LICENSE_FILES_TO_PACKAGE = { "LICENSE.txt", "THIRDPARTYNOTICES.txt" };

	/// <summary>
	/// Steamworks redist files the dedicated server needs next to its executable. (Steam's own server download
	/// includes these, so a build we hand out has to as well.)
	/// </summary>
	private static readonly string[] SERVER_STEAM_REDIST_FILES = { "steamclient64.dll", "tier0_s64.dll", "vstdlib_s64.dll" };

	private const string SERVER_NAME = "MyServer";

	private const string PACKAGE_README = @"This is a modified game based on Unturned, made with the U3 SDK.
References to UNTURNED are only to identify what this mod is based on and are not used as a trademark.
This mod is not affiliated with or endorsed by Smartly Dressed Games Ltd., and does not make, use or sell any goods
or services provided by Smartly Dressed Games Ltd. It is free and non-commercial. See LICENSE.txt and
THIRDPARTYNOTICES.txt.

PLAYING
- Steam must be running, and Unturned (free on Steam) must be installed. The game loads its core assets from there.
- Run Unturned.exe from this folder (not the Steam version).
- Everyone, including the server, needs this exact build. Regular Unturned clients and servers can't join.
";

	private const string SERVER_README = @"

HOSTING
- Run StartServer.bat. Server settings are created in Servers/" + SERVER_NAME + @"/ the first time it runs.
- For friends to join over the internet, forward the server's ports (UDP 27015-27016 by default).
";

	public static bool IsWindowsDedicatedServerModuleInstalled()
	{
		string variationsPath = Path.Combine(EditorApplication.applicationContentsPath, "PlaybackEngines", "windowsstandalonesupport", "Variations");
		return Directory.Exists(Path.Combine(variationsPath, "win64_server_nondevelopment_mono"));
	}

	/// <summary>
	/// Build the Windows 64-bit game (plus the dedicated server if Unity's Windows Dedicated Server Build Support is
	/// installed) and package each into Builds/Packages as a folder and a zip ready to send.
	/// </summary>
	public static bool BuildAndPackageWindows64()
	{
		scriptsOnly = false;

		bool buildServer = IsWindowsDedicatedServerModuleInstalled();
		if (buildServer)
		{
			// Server first so the editor ends up targeting the Windows player again.
			if (!runBuildStep("Building Windows64 server", buildWindows64DedicatedServer))
				return false;
		}
		else
		{
			CLCIReport.logWarning("Skipping the dedicated server: install \"Windows Dedicated Server Build Support\" for this editor version in Unity Hub to build it.");
		}

		if (!runBuildStep("Building Windows64 player", buildWindows64Player))
			return false;

		if (!runBuildStep("Packaging Windows64 game", PackageWindows64Player))
			return false;

		if (buildServer)
		{
			if (!runBuildStep("Packaging Windows64 server", PackageWindows64Server))
				return false;
		}

		EditorUtility.RevealInFinder(GetPackagesPath());
		return true;
	}

	private static string GetPackagesPath()
	{
		return Path.Combine(GetBuildsPath(), "Packages");
	}

	private static bool PackageWindows64Player()
	{
		return PackageBuild("Windows64", "Windows64", PACKAGE_README);
	}

	private static bool PackageWindows64Server()
	{
		string steamRedistPath = Path.Combine(SteamCmdUtils.GetSteamAppsPath(), "content", "app_1007", "depot_1004");
		if (!Directory.Exists(steamRedistPath))
		{
			if (!runBuildStep("Downloading Steam dedicated server SDK redist", UpdateSteamDedicatedServerRedist))
				return false;
		}

		if (!PackageBuild("Windows64_Headless", "Windows64_Server", PACKAGE_README + SERVER_README))
			return false;

		string packagePath = Path.Combine(GetPackagesPath(), "Windows64_Server");
		foreach (string fileName in SERVER_STEAM_REDIST_FILES)
		{
			File.Copy(Path.Combine(steamRedistPath, fileName), Path.Combine(packagePath, fileName), true);
		}

		File.WriteAllText(Path.Combine(packagePath, "StartServer.bat"), "@echo off\r\ncd /d \"%~dp0\"\r\nUnturned.exe -batchmode -nographics +InternetServer/" + SERVER_NAME + "\r\n");

		return ZipPackage("Windows64_Server");
	}

	/// <summary>
	/// Copy Builds/[buildFolderName] into Builds/Packages/[packageName] without debug-only files, then add the shared
	/// game files, license files and a readme.
	/// </summary>
	private static bool PackageBuild(string buildFolderName, string packageName, string readme)
	{
		string buildPath = Path.Combine(GetBuildsPath(), buildFolderName);
		if (!Directory.Exists(buildPath))
		{
			CLCIReport.logError($"Build folder \"{buildPath}\" missing");
			return false;
		}

		string packagePath = Path.Combine(GetPackagesPath(), packageName);
		if (Directory.Exists(packagePath))
		{
			Directory.Delete(packagePath, true);
		}

		CopyBuildDirectory(buildPath, packagePath);

		string sharedPath = Path.Combine(GetBuildsPath(), "Shared");
		foreach (string fileName in SHARED_FILES_TO_PACKAGE)
		{
			string sourcePath = Path.Combine(sharedPath, fileName);
			if (!File.Exists(sourcePath))
			{
				CLCIReport.logError($"Shared file \"{sourcePath}\" missing");
				return false;
			}

			File.Copy(sourcePath, Path.Combine(packagePath, fileName), true);
		}

		foreach (string fileName in LICENSE_FILES_TO_PACKAGE)
		{
			string sourcePath = Path.Combine(UnityPaths.ProjectDirectory.FullName, fileName);
			if (!File.Exists(sourcePath))
			{
				CLCIReport.logError($"License file \"{sourcePath}\" missing");
				return false;
			}

			File.Copy(sourcePath, Path.Combine(packagePath, fileName), true);
		}

		File.WriteAllText(Path.Combine(packagePath, "README.txt"), readme.Replace("\n", "\r\n"));

		// The server zip is made after its extra files are added.
		if (packageName == "Windows64_Server")
			return true;

		return ZipPackage(packageName);
	}

	private static bool ZipPackage(string packageName)
	{
		string packagePath = Path.Combine(GetPackagesPath(), packageName);
		string zipPath = packagePath + ".zip";
		if (File.Exists(zipPath))
		{
			File.Delete(zipPath);
		}

		ZipFile.CreateFromDirectory(packagePath, zipPath, CompressionLevel.Optimal, includeBaseDirectory: true);
		CLCIReport.log($"Packaged \"{zipPath}\"");
		return true;
	}

	/// <summary>
	/// Recursive copy that leaves out debug symbols, Burst debug info and logs from playing the build locally.
	/// </summary>
	private static void CopyBuildDirectory(string sourcePath, string destinationPath)
	{
		Directory.CreateDirectory(destinationPath);

		foreach (string filePath in Directory.GetFiles(sourcePath))
		{
			string fileName = Path.GetFileName(filePath);
			if (fileName.EndsWith(".pdb", System.StringComparison.OrdinalIgnoreCase))
				continue;

			File.Copy(filePath, Path.Combine(destinationPath, fileName), true);
		}

		foreach (string directoryPath in Directory.GetDirectories(sourcePath))
		{
			string directoryName = Path.GetFileName(directoryPath);
			if (directoryName.EndsWith("_DoNotShip", System.StringComparison.OrdinalIgnoreCase)
				|| directoryName == "Logs")
				continue;

			CopyBuildDirectory(directoryPath, Path.Combine(destinationPath, directoryName));
		}
	}
}
