using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace StealYourVoice.Voicemeeter;

/// <summary>
/// Raw P/Invoke bindings for VoicemeeterRemote64.dll (official VB-Audio Remote API).
/// The DLL ships with Voicemeeter itself, so we locate the install folder at runtime.
/// </summary>
internal static class VmNative
{
    private const string Dll = "VoicemeeterRemote64.dll";
    private static bool _resolverSet;

    public static string? DllPath { get; private set; }

    public static bool TryInit(out string error)
    {
        error = "";
        DllPath = FindDll();
        if (DllPath == null)
        {
            error = "Voicemeeter не найден. Установи Voicemeeter Banana с vb-audio.com.";
            return false;
        }

        if (!_resolverSet)
        {
            NativeLibrary.SetDllImportResolver(Assembly.GetExecutingAssembly(), (name, _, _) =>
                name == Dll && DllPath != null ? NativeLibrary.Load(DllPath) : IntPtr.Zero);
            _resolverSet = true;
        }
        return true;
    }

    private static string? FindDll()
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\VB:Voicemeeter {17359A74-1236-5467}";
        foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
        {
            try
            {
                using var hklm = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var k = hklm.OpenSubKey(key);
                var uninstall = k?.GetValue("UninstallString") as string;
                if (!string.IsNullOrWhiteSpace(uninstall))
                {
                    var dir = Path.GetDirectoryName(uninstall.Trim('"'));
                    if (dir != null)
                    {
                        var p = Path.Combine(dir, Dll);
                        if (File.Exists(p)) return p;
                    }
                }
            }
            catch { /* ignore and try next */ }
        }

        foreach (var root in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                 })
        {
            var p = Path.Combine(root, "VB", "Voicemeeter", Dll);
            if (File.Exists(p)) return p;
        }
        return null;
    }

    [DllImport(Dll, EntryPoint = "VBVMR_Login", CallingConvention = CallingConvention.StdCall)]
    public static extern int Login();

    [DllImport(Dll, EntryPoint = "VBVMR_Logout", CallingConvention = CallingConvention.StdCall)]
    public static extern int Logout();

    [DllImport(Dll, EntryPoint = "VBVMR_RunVoicemeeter", CallingConvention = CallingConvention.StdCall)]
    public static extern int RunVoicemeeter(int type);

    [DllImport(Dll, EntryPoint = "VBVMR_GetVoicemeeterType", CallingConvention = CallingConvention.StdCall)]
    public static extern int GetVoicemeeterType(out int type);

    [DllImport(Dll, EntryPoint = "VBVMR_IsParametersDirty", CallingConvention = CallingConvention.StdCall)]
    public static extern int IsParametersDirty();

    [DllImport(Dll, EntryPoint = "VBVMR_GetParameterFloat", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int GetParameterFloat(string name, out float value);

    [DllImport(Dll, EntryPoint = "VBVMR_GetParameterStringA", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int GetParameterStringA(string name, StringBuilder value);

    [DllImport(Dll, EntryPoint = "VBVMR_SetParameterFloat", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int SetParameterFloat(string name, float value);

    [DllImport(Dll, EntryPoint = "VBVMR_SetParameters", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int SetParameters(string script);

    [DllImport(Dll, EntryPoint = "VBVMR_GetLevel", CallingConvention = CallingConvention.StdCall)]
    public static extern int GetLevel(int type, int channel, out float value);

    [DllImport(Dll, EntryPoint = "VBVMR_Input_GetDeviceNumber", CallingConvention = CallingConvention.StdCall)]
    public static extern int InputGetDeviceNumber();

    [DllImport(Dll, EntryPoint = "VBVMR_Input_GetDeviceDescA", CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Ansi)]
    public static extern int InputGetDeviceDescA(int index, out int type, StringBuilder name, StringBuilder hardwareId);
}
