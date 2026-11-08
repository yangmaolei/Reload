using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using GameSaveSync.Setup;

namespace GameSaveSync.Setup.Tests;

/// <summary>
/// 安装器核心逻辑测试。
/// 覆盖：路径规划、目录校验、参数解析、向导状态机、真实文件系统的安装与卸载。
/// </summary>
public sealed class InstallerTests : IDisposable
{
    private readonly string _root;

    public InstallerTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "reload-setup-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }
        catch
        {
            // 清理失败不影响测试结论
        }
    }

    // ---------- 路径规划 ----------

    [Fact]
    public void DefaultCurrentUserDirectory_UnderLocalAppDataPrograms()
    {
        var dir = SetupPaths.DefaultCurrentUserDirectory();

        Assert.Contains("Programs", dir);
        Assert.EndsWith("Reload", dir);
    }

    [Fact]
    public void DefaultAllUsersDirectory_UnderProgramFiles()
    {
        var dir = SetupPaths.DefaultAllUsersDirectory();

        Assert.StartsWith(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            dir,
            StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("Reload", dir);
    }

    [Fact]
    public void DefaultDirectory_FollowsScope()
    {
        Assert.Equal(
            SetupPaths.DefaultCurrentUserDirectory(),
            SetupPaths.DefaultDirectory(InstallScope.CurrentUser));

        Assert.Equal(
            SetupPaths.DefaultAllUsersDirectory(),
            SetupPaths.DefaultDirectory(InstallScope.AllUsers));
    }

    [Fact]
    public void InstallPlan_RequiresElevation_OnlyForAllUsers()
    {
        var current = new InstallPlan { Scope = InstallScope.CurrentUser, InstallDirectory = _root };
        var all = new InstallPlan { Scope = InstallScope.AllUsers, InstallDirectory = _root };

        Assert.False(current.RequiresElevation);
        Assert.True(all.RequiresElevation);
    }

    [Fact]
    public void InstallPlan_ExecutablePath_CombinesDirectoryAndName()
    {
        var plan = new InstallPlan
        {
            Scope = InstallScope.CurrentUser,
            InstallDirectory = Path.Combine(_root, "Reload"),
        };

        Assert.Equal(Path.Combine(_root, "Reload", "Reload.exe"), plan.ExecutablePath);
    }

    [Fact]
    public void InstallPlan_Shortcuts_NullWhenDisabled()
    {
        var withShortcut = new InstallPlan
        {
            Scope = InstallScope.CurrentUser,
            InstallDirectory = _root,
            CreateShortcut = true,
        };

        var withoutShortcut = new InstallPlan
        {
            Scope = InstallScope.CurrentUser,
            InstallDirectory = _root,
            CreateShortcut = false,
        };

        Assert.NotNull(withShortcut.DesktopShortcutPath);
        Assert.NotNull(withShortcut.StartMenuShortcutPath);
        Assert.Null(withoutShortcut.DesktopShortcutPath);
        Assert.Null(withoutShortcut.StartMenuShortcutPath);
    }

    [Fact]
    public void InstallPlan_AutoStartHive_FollowsScope()
    {
        var current = new InstallPlan { Scope = InstallScope.CurrentUser, InstallDirectory = _root };
        var all = new InstallPlan { Scope = InstallScope.AllUsers, InstallDirectory = _root };

        Assert.Equal(Microsoft.Win32.RegistryHive.CurrentUser, current.AutoStartHive);
        Assert.Equal(Microsoft.Win32.RegistryHive.LocalMachine, all.AutoStartHive);
        Assert.Equal(Microsoft.Win32.RegistryHive.CurrentUser, current.UninstallHive);
        Assert.Equal(Microsoft.Win32.RegistryHive.LocalMachine, all.UninstallHive);
    }

    [Fact]
    public void InstallPlan_UninstallCommand_PointsToInstaller()
    {
        var plan = new InstallPlan { Scope = InstallScope.CurrentUser, InstallDirectory = _root };

        Assert.Contains(SetupPaths.InstallerExePath, plan.UninstallCommand);
        Assert.Contains(SetupPaths.UninstallArgument, plan.UninstallCommand);
    }

    // ---------- 目录校验 ----------

    [Fact]
    public void IsDirectoryAcceptable_RejectsEmpty()
    {
        Assert.False(SetupPaths.IsDirectoryAcceptable("", out var reason));
        Assert.NotEmpty(reason);
    }

    [Fact]
    public void IsDirectoryAcceptable_RejectsDriveRoot()
    {
        var root = Path.GetPathRoot(Path.GetFullPath(_root));

        Assert.False(SetupPaths.IsDirectoryAcceptable(root!, out var reason));
        Assert.Contains("根目录", reason);
    }

    [Fact]
    public void IsDirectoryAcceptable_AcceptsNormalPath()
    {
        Assert.True(SetupPaths.IsDirectoryAcceptable(Path.Combine(_root, "Reload"), out var reason));
        Assert.Empty(reason);
    }

    // ---------- 命令行参数 ----------

    [Fact]
    public void SetupArguments_Restore_Empty()
    {
        var restored = SetupArgumentsRestore(Array.Empty<string>());

        Assert.Equal(InstallScope.CurrentUser, restored.Scope);
        Assert.True(restored.CreateShortcut);
        Assert.False(restored.EnableAutoStart);
        Assert.Null(restored.InstallDirectory);
    }

    [Fact]
    public void SetupArguments_Restore_AllFields()
    {
        var restored = SetupArgumentsRestore(new[]
        {
            "--elevated",
            "--scope=AllUsers",
            "--dir=\"D:\\Apps\\Reload\"",
            "--shortcut=0",
            "--autostart=1",
        });

        Assert.Equal(InstallScope.AllUsers, restored.Scope);
        Assert.Equal("D:\\Apps\\Reload", restored.InstallDirectory);
        Assert.False(restored.CreateShortcut);
        Assert.True(restored.EnableAutoStart);
    }

    [Fact]
    public void SetupArguments_Restore_IgnoresUnknownFlags()
    {
        var restored = SetupArgumentsRestore(new[] { "--unknown=1", "--scope=AllUsers" });

        Assert.Equal(InstallScope.AllUsers, restored.Scope);
        Assert.True(restored.CreateShortcut);
    }

    // ---------- 向导状态机 ----------

    [Fact]
    public void ViewModel_StartsAtWelcome()
    {
        var vm = NewViewModel();

        Assert.Equal(SetupStep.Welcome, vm.Current);
        Assert.Equal(1, vm.CurrentStepIndex);
        Assert.False(vm.CanGoBack);
        Assert.True(vm.CanGoNext);
    }

    [Fact]
    public async Task ViewModel_NextAdvancesThroughSteps()
    {
        var vm = NewViewModel();

        await vm.GoNextAsync();
        Assert.Equal(SetupStep.Scope, vm.Current);

        await vm.GoNextAsync();
        Assert.Equal(SetupStep.Location, vm.Current);

        await vm.GoNextAsync();
        Assert.Equal(SetupStep.Options, vm.Current);
    }

    [Fact]
    public async Task ViewModel_BackGoesPreviousStep()
    {
        var vm = NewViewModel();

        await vm.GoNextAsync();
        await vm.GoNextAsync();
        Assert.Equal(SetupStep.Location, vm.Current);

        vm.GoBack();
        Assert.Equal(SetupStep.Scope, vm.Current);

        vm.GoBack();
        Assert.Equal(SetupStep.Welcome, vm.Current);

        // 欢迎页不能再退
        vm.GoBack();
        Assert.Equal(SetupStep.Welcome, vm.Current);
    }

    [Fact]
    public void ViewModel_ScopeChange_UpdatesDefaultDirectory()
    {
        var vm = NewViewModel();

        vm.Scope = InstallScope.AllUsers;
        Assert.Equal(SetupPaths.DefaultAllUsersDirectory(), vm.InstallDirectory);

        vm.Scope = InstallScope.CurrentUser;
        Assert.Equal(SetupPaths.DefaultCurrentUserDirectory(), vm.InstallDirectory);
    }

    [Fact]
    public void ViewModel_RequiresElevationNotice_OnlyForAllUsers()
    {
        var vm = NewViewModel();

        Assert.False(vm.RequiresElevationNotice);

        vm.Scope = InstallScope.AllUsers;
        Assert.True(vm.RequiresElevationNotice);
    }

    [Fact]
    public void ViewModel_InvalidDirectory_ReportsError()
    {
        var vm = NewViewModel();

        vm.InstallDirectory = "";
        Assert.False(vm.IsDirectoryValid);
        Assert.NotEmpty(vm.DirectoryError);

        vm.InstallDirectory = Path.Combine(_root, "Reload");
        Assert.True(vm.IsDirectoryValid);
        Assert.Empty(vm.DirectoryError);
    }

    [Fact]
    public void ViewModel_BuildPlan_CapturesChoices()
    {
        var vm = NewViewModel();
        vm.Scope = InstallScope.CurrentUser;
        vm.InstallDirectory = Path.Combine(_root, "Reload");
        vm.CreateShortcut = false;
        vm.EnableAutoStart = true;

        var plan = vm.BuildPlan();

        Assert.Equal(InstallScope.CurrentUser, plan.Scope);
        Assert.Equal(Path.Combine(_root, "Reload"), plan.InstallDirectory);
        Assert.False(plan.CreateShortcut);
        Assert.True(plan.EnableAutoStart);
    }

    [Fact]
    public async Task ViewModel_InstallWithInvalidDirectory_FailsFast()
    {
        var vm = NewViewModel();
        vm.InstallDirectory = "";

        await vm.InstallAsync();

        Assert.False(vm.FinishedSuccessfully);
        Assert.Equal(SetupStep.Finished, vm.Current);
        Assert.NotEmpty(vm.FinishedMessage);
    }

    // ---------- 真实文件系统上的安装 / 卸载 ----------

    [Fact]
    public async Task Install_CopiesFilesAndCreatesDirectories()
    {
        var payload = CreateFakePayload();
        var target = Path.Combine(_root, "install-target");

        var installer = new Installer(payload);
        var plan = new InstallPlan
        {
            Scope = InstallScope.CurrentUser,
            InstallDirectory = target,
            CreateShortcut = false,
        };

        var outcome = await installer.RunAsync(plan, installer.BuildSteps(plan));

        Assert.True(outcome.Success, outcome.Message);
        Assert.True(File.Exists(Path.Combine(target, "Reload.exe")));
        Assert.Contains("创建安装目录", outcome.CompletedSteps);
        Assert.Contains("复制程序文件", outcome.CompletedSteps);
    }

    [Fact]
    public async Task Install_CopiesFileContentIntact()
    {
        var payload = CreateFakePayload();
        var target = Path.Combine(_root, "install-target");

        var installer = new Installer(payload);
        var plan = new InstallPlan
        {
            Scope = InstallScope.CurrentUser,
            InstallDirectory = target,
            CreateShortcut = false,
        };

        await installer.RunAsync(plan, installer.BuildSteps(plan));

        var installed = File.ReadAllBytes(Path.Combine(target, "Reload.exe"));
        var original = File.ReadAllBytes(payload);

        Assert.Equal(original.Length, installed.Length);
        Assert.Equal(original, installed);
    }

    [Fact]
    public async Task Install_OverwritesExistingFile()
    {
        var payload = CreateFakePayload();
        var target = Path.Combine(_root, "install-target");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "Reload.exe"), "old junk content");

        var installer = new Installer(payload);
        var plan = new InstallPlan
        {
            Scope = InstallScope.CurrentUser,
            InstallDirectory = target,
            CreateShortcut = false,
        };

        var outcome = await installer.RunAsync(plan, installer.BuildSteps(plan));

        Assert.True(outcome.Success, outcome.Message);
        Assert.NotEqual("old junk content", File.ReadAllText(Path.Combine(target, "Reload.exe")));
    }

    [Fact]
    public async Task Install_FailsWhenSourceMissing()
    {
        var missing = Path.Combine(_root, "does-not-exist.exe");
        var target = Path.Combine(_root, "install-target");

        var installer = new Installer(missing);
        var plan = new InstallPlan
        {
            Scope = InstallScope.CurrentUser,
            InstallDirectory = target,
            CreateShortcut = false,
        };

        var outcome = await installer.RunAsync(plan, installer.BuildSteps(plan));

        Assert.False(outcome.Success);
        Assert.Contains("复制程序文件", outcome.FailedSteps);
        Assert.Contains("失败", outcome.Message);
    }

    [Fact]
    public async Task Install_StopsAtFirstFailure()
    {
        var missing = Path.Combine(_root, "does-not-exist.exe");
        var target = Path.Combine(_root, "install-target");

        var installer = new Installer(missing);
        var plan = new InstallPlan
        {
            Scope = InstallScope.CurrentUser,
            InstallDirectory = target,
            CreateShortcut = false,
        };

        var outcome = await installer.RunAsync(plan, installer.BuildSteps(plan));

        Assert.False(outcome.Success);

        // 复制失败后，后续步骤不应执行
        Assert.DoesNotContain("注册卸载程序", outcome.CompletedSteps);
    }

    [Fact]
    public async Task Uninstall_RemovesInstalledFiles()
    {
        var payload = CreateFakePayload();
        var target = Path.Combine(_root, "install-target");

        var installer = new Installer(payload);
        var plan = new InstallPlan
        {
            Scope = InstallScope.CurrentUser,
            InstallDirectory = target,
            CreateShortcut = false,
        };

        await installer.RunAsync(plan, installer.BuildSteps(plan));
        Assert.True(Directory.Exists(target));

        var outcome = Installer.Uninstall(plan);

        Assert.True(outcome.Success, outcome.Message);
        Assert.False(Directory.Exists(target));
    }

    [Fact]
    public void Uninstall_OnNonExistentInstall_StillSucceeds()
    {
        var plan = new InstallPlan
        {
            Scope = InstallScope.CurrentUser,
            InstallDirectory = Path.Combine(_root, "never-installed"),
            CreateShortcut = false,
        };

        var outcome = Installer.Uninstall(plan);

        Assert.True(outcome.Success, outcome.Message);
    }

    [Fact]
    public async Task Install_ThenUninstall_RemovesUninstallRegistryKey()
    {
        // 回归测试：曾经用 DeleteValue(null) 只删了默认值，
        // 结果「应用和功能」里的卸载项还在 —— 键本身必须删掉。
        var payload = CreateFakePayload();
        var target = Path.Combine(_root, "install-target");

        var installer = new Installer(payload);
        var plan = new InstallPlan
        {
            Scope = InstallScope.CurrentUser,
            InstallDirectory = target,
            CreateShortcut = false,
            // 用独立注册表路径，避免动到用户真实的卸载项
            UninstallKeyPathOverride = @"SOFTWARE\ReloadSetupTests\Uninstall\Reload",
        };

        await installer.RunAsync(plan, installer.BuildSteps(plan));
        Assert.True(RegistryKeyExists(plan), "卸载注册表项应已写入");

        var outcome = Installer.Uninstall(plan);

        Assert.True(outcome.Success, outcome.Message);
        Assert.False(RegistryKeyExists(plan), "卸载注册表项应已删除");
    }

    [Fact]
    public async Task Install_ThenUninstall_RemovesAutoStartEntry()
    {
        var payload = CreateFakePayload();
        var target = Path.Combine(_root, "install-target");

        var installer = new Installer(payload);
        var plan = new InstallPlan
        {
            Scope = InstallScope.CurrentUser,
            InstallDirectory = target,
            CreateShortcut = false,
            EnableAutoStart = true,
            // 用独立注册表路径，避免动到用户真实的自启项
            AutoStartKeyPathOverride = @"SOFTWARE\ReloadSetupTests\Run",
            AutoStartValueNameOverride = "ReloadSetupTest",
            UninstallKeyPathOverride = @"SOFTWARE\ReloadSetupTests\Uninstall\Reload",
        };

        await installer.RunAsync(plan, installer.BuildSteps(plan));
        Assert.True(Installer.IsAutoStartEnabled(plan), "自启项应已写入");

        Installer.Uninstall(plan);

        Assert.False(Installer.IsAutoStartEnabled(plan), "自启项应已删除");
    }

    /// <summary>检查卸载注册表键是否存在。</summary>
    private static bool RegistryKeyExists(InstallPlan plan)
    {
        try
        {
            using var key = Microsoft.Win32.RegistryKey.OpenBaseKey(
                plan.UninstallHive,
                Microsoft.Win32.RegistryView.Registry64)
                .OpenSubKey(plan.UninstallKeyPath, writable: false);

            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    // ---------- 辅助 ----------

    private static SetupArguments SetupArgumentsRestore(string[] args) => SetupArguments.Restore(args);

    private SetupViewModel NewViewModel() => new SetupViewModel(new Installer(CreateFakePayload()));

    /// <summary>造一个假的 payload exe（有内容即可，不需要能运行）。</summary>
    private string CreateFakePayload()
    {
        var path = Path.Combine(_root, "payload", "Reload.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[] { 0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00 });
        return path;
    }
}
