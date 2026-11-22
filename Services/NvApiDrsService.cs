using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using CrimsonDesertDlssFixer.Models;

namespace CrimsonDesertDlssFixer.Services;

public sealed class RawDrsBinRecord
{
    public uint SettingId { get; init; }
    public uint Flags { get; init; }
    public uint Value { get; init; }
    public bool IsUserModified => (Flags & 0x2) != 0;
}

public sealed class DrsSettingSnapshot
{
    public uint SettingId { get; init; }
    public string SettingName { get; init; } = string.Empty;
    public string FriendlyName { get; init; } = string.Empty;
    public uint SettingType { get; init; }
    public uint SettingLocation { get; init; }
    public bool IsCurrentPredefined { get; init; }
    public bool IsPredefinedValid { get; init; }
    public uint CurrentU32 { get; init; }
    public uint PredefinedU32 { get; init; }
    public string CurrentString { get; init; } = string.Empty;
    public string PredefinedString { get; init; } = string.Empty;
    public bool IsFromBaseProfile { get; init; }
    public string DecodedCurrentValue { get; init; } = string.Empty;
}

public sealed class NvApiDrsService
{
    private const int NVDRS_SETTING_SIZE = 12320;
    private const uint NVDRS_SETTING_VER = NVDRS_SETTING_SIZE | (1u << 16);

    private const int OFF_VERSION = 0;
    private const int OFF_NAME = 4;
    private const int OFF_ID = 4100;
    private const int OFF_TYPE = 4104;
    private const int OFF_LOCATION = 4108;
    private const int OFF_IS_PREDEF = 4112;
    private const int OFF_IS_PREDEF_VALID = 4116;
    private const int OFF_PREDEF_VAL = 4120;
    private const int OFF_CURR_VAL = 8220;

    // Known NVIDIA DRS setting IDs touched by RHI, NVIDIA App, NVPI, DLSS, MFG & Streamline overrides
    public static readonly IReadOnlyDictionary<uint, (string Name, string Role, bool AffectsMfg)> KnownDlssMfgIds =
        new Dictionary<uint, (string, string, bool)>
        {
            [0x10E41E06] = ("Enable Streamline override", "Forces NVIDIA OTA Streamline DLL injection (overrides game's native sl.*.dll)", true),
            [0x10308298] = ("Override DLSSG mode", "Forces DLSS Frame Generation / MFG mode (e.g. Fixed MFG / Dynamic MFG)", true),
            [0x104D6667] = ("Override DLSSG multi-frame count", "Forces MFG generated frame multiplier count (2x-6x)", true),
            [0x10562D0F] = ("DLSS-FG Dynamic Target / MFG parameter", "Overrides DLSS-FG / MFG dynamic target parameter", true),
            [0x10CF4125] = ("DLSS-FG / MFG extended override", "Overrides extended DLSS-FG / MFG driver setting", true),
            [0x10E41E03] = ("Enable DLSS-FG override", "Overrides DLSS Frame Generation model/DLL (nvngx_dlssg)", true),
            [0x10E41E01] = ("Enable DLSS-SR override", "Overrides DLSS Super Resolution model/DLL (nvngx_dlss)", false),
            [0x10E41E02] = ("Enable DLSS-RR override", "Overrides DLSS Ray Reconstruction model/DLL (nvngx_dlssd)", false),
            [0x10E41E04] = ("Enable DLSS-NR override", "Overrides DLSS Neural Rendering (DLSS 5)", false),
            [0x10E41E05] = ("Enable NGX override (extended)", "Extended NGX driver override flag", true),
            [0x10AFB768] = ("Override DLSS-SR performance mode", "Forces DLSS Super Resolution quality/performance mode (overrides in-game menu)", false),
            [0x10BD9423] = ("Override DLSS-RR performance mode", "Forces DLSS Ray Reconstruction quality/performance mode", false),
            [0x10E41DF5] = ("Override scaling ratio for DLSS-SR", "Forces custom DLSS-SR internal render scaling ratio", false),
            [0x10C7D4A2] = ("Override scaling ratio for DLSS-RR", "Forces custom DLSS-RR internal render scaling ratio", false),
            [0x10E41DF1] = ("Override DLSS-SR presets", "Forces specific DLSS-SR AI preset (A-M / Latest)", false),
            [0x10E41DF3] = ("Override DLSS-RR presets", "Forces specific DLSS-RR AI preset", false),
            [0x10E41DF7] = ("Override DLSS-FG presets", "Forces specific DLSS-FG / MFG preset", true),
            [0x10E41DF8] = ("Override DLSS-NR presets", "Forces specific DLSS Neural Rendering preset", false),
            [0x10E41DF9] = ("Override DLSS-SR preset (extended)", "Forces DLSS-SR preset override (extended)", false),
            [0x10E41DFA] = ("Override DLSS-RR preset (extended)", "Forces DLSS-RR preset override (extended)", false),
            [0x10E41DFB] = ("Override DLSS-FG preset (extended)", "Forces DLSS-FG / MFG preset override (extended)", true),
        };

    // Friendly names for private / non-DLSS DRS settings inspected via nvdrsdb0/1.bin (not auto-deleted from Global Base Profile)
    private static readonly IReadOnlyDictionary<uint, string> AdditionalFriendlyNames =
        new Dictionary<uint, string>
        {
            [0xB0D384C0] = "NVIDIA Smooth Motion (DriverFGX)",
            [0x0005F543] = "Low Latency Mode",
            [0x00DD48FB] = "Enable TrueHDR Feature (RTX HDR)",
            [0x00634291] = "DLSS Model Preset Profile",
            [0x00A879CF] = "Vertical Sync",
        };

    public const uint DRS_SMOOTH_MOTION = 0xB0D384C0;
    public const uint DRS_LOW_LATENCY_MODE = 0x0005F543;
    public const uint DRS_RTX_HDR_ENABLE = 0x00DD48FB;
    public const uint DRS_DLSS_MODEL_PRESET_PROFILE = 0x00634291;
    public const uint DRS_VSYNC = 0x00A879CF;
    public const uint VSYNC_FORCE_ON = 0x47814940;
    public const uint VSYNC_USE_APP = 0x00000000;
    public const uint VSYNC_FORCE_OFF = 0x08416747;

    // Function IDs from NVIDIA NVAPI SDK (with internal v2 IDs that support private DRS settings like Smooth Motion & RTX HDR)
    private const uint FID_Initialize = 0x0150E828;
    private const uint FID_Unload = 0xD22BDD7E;
    private const uint FID_DRS_CreateSession = 0x0694D52E;
    private const uint FID_DRS_DestroySession = 0xDAD9CFF8;
    private const uint FID_DRS_LoadSettings = 0x375DBD6B;
    private const uint FID_DRS_SaveSettings = 0xFCBC7E14;
    private const uint FID_DRS_GetBaseProfile = 0xDA8466A0;
    private const uint FID_DRS_FindProfileByName = 0x7E4A9A0B;
    private const uint FID_DRS_CreateProfile = 0xCC176068;
    private const uint FID_DRS_CreateApplication = 0x4347A9DE;
    private const uint FID_DRS_GetSetting_Ex = 0xEA99498D;
    private const uint FID_DRS_GetSetting = 0x73BF8338;
    private const uint FID_DRS_EnumSettings_Ex = 0xCFD6983E;
    private const uint FID_DRS_EnumSettings = 0xAE3039DA;
    private const uint FID_DRS_SetSetting_Ex = 0x8A2CF5F5;
    private const uint FID_DRS_SetSetting = 0x577DD202;
    private const uint FID_DRS_DeleteProfileSetting_Ex = 0xD20D29DF;
    private const uint FID_DRS_DeleteProfileSetting = 0xE4A26362;
    private const uint FID_DRS_RestoreProfileDefaultSetting = 0x53F0381E;
    private const uint FID_DRS_GetSettingNameFromId_Ex = 0x1EB13791;
    private const uint FID_DRS_GetSettingNameFromId = 0xD61CBE6E;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiInitializeDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiUnloadDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiDrsCreateSessionDelegate(out IntPtr hSession);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiDrsDestroySessionDelegate(IntPtr hSession);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiDrsSessionOnlyDelegate(IntPtr hSession);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiDrsGetBaseProfileDelegate(IntPtr hSession, out IntPtr hProfile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiDrsFindProfileByNameDelegate(
        IntPtr hSession,
        [MarshalAs(UnmanagedType.LPWStr)] string profileName,
        out IntPtr hProfile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiDrsCreateProfileDelegate(IntPtr hSession, IntPtr pProfileInfo, out IntPtr phProfile);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiDrsCreateApplicationDelegate(IntPtr hSession, IntPtr hProfile, IntPtr pApplication);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiDrsGetSettingDelegate(IntPtr hSession, IntPtr hProfile, uint settingId, IntPtr pSetting);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiDrsEnumSettingsDelegate(
        IntPtr hSession,
        IntPtr hProfile,
        uint startIndex,
        ref uint settingsCount,
        IntPtr pSetting);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiDrsSetSettingDelegate(IntPtr hSession, IntPtr hProfile, IntPtr pSetting);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiDrsSetSettingExDelegate(IntPtr hSession, IntPtr hProfile, IntPtr pSetting, uint x, uint y);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiDrsGetSettingExDelegate(IntPtr hSession, IntPtr hProfile, uint settingId, IntPtr pSetting, ref uint x);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiDrsSettingIdOpDelegate(IntPtr hSession, IntPtr hProfile, uint settingId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int NvApiDrsGetSettingNameFromIdDelegate(uint settingId, IntPtr pNameBuffer);

    [DllImport("nvapi64.dll", EntryPoint = "nvapi_QueryInterface", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr NvApiQueryInterface(uint functionId);

    private static T? GetProc<T>(uint functionId) where T : Delegate
    {
        IntPtr ptr = NvApiQueryInterface(functionId);
        return ptr == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(ptr);
    }

    private static T? GetProcWithFallback<T>(uint primaryId, uint fallbackId) where T : Delegate =>
        GetProc<T>(primaryId) ?? GetProc<T>(fallbackId);

    public static bool IsNvApiAvailable()
    {
        try
        {
            return GetProc<NvApiInitializeDelegate>(FID_Initialize) is not null;
        }
        catch
        {
            return false;
        }
    }

    public (List<DrsSettingSnapshot> GlobalOverrides, List<DrsSettingSnapshot> ProfileOverrides, string ProfileNameFound, string? Error)
        ScanDrsOverrides()
    {
        var globalOverrides = new List<DrsSettingSnapshot>();
        var profileOverrides = new List<DrsSettingSnapshot>();
        string profileNameFound = "Crimson Desert";

        try
        {
            var init = GetProc<NvApiInitializeDelegate>(FID_Initialize);
            var createSession = GetProc<NvApiDrsCreateSessionDelegate>(FID_DRS_CreateSession);
            var loadSettings = GetProc<NvApiDrsSessionOnlyDelegate>(FID_DRS_LoadSettings);
            var destroySession = GetProc<NvApiDrsDestroySessionDelegate>(FID_DRS_DestroySession);
            var getBase = GetProc<NvApiDrsGetBaseProfileDelegate>(FID_DRS_GetBaseProfile);
            var findProfile = GetProc<NvApiDrsFindProfileByNameDelegate>(FID_DRS_FindProfileByName);
            var getSetting = GetProc<NvApiDrsGetSettingDelegate>(FID_DRS_GetSetting);
            var enumSettings = GetProc<NvApiDrsEnumSettingsDelegate>(FID_DRS_EnumSettings);
            var getSettingName = GetProc<NvApiDrsGetSettingNameFromIdDelegate>(FID_DRS_GetSettingNameFromId);

            if (init is null || createSession is null || loadSettings is null || destroySession is null ||
                getBase is null || findProfile is null || getSetting is null || enumSettings is null)
            {
                return (globalOverrides, profileOverrides, profileNameFound, "nvapi64.dll DRS functions unavailable.");
            }

            int rc = init();
            if (rc != 0)
            {
                return (globalOverrides, profileOverrides, profileNameFound, $"NvAPI_Initialize failed (rc={rc}).");
            }

            rc = createSession(out IntPtr session);
            if (rc != 0 || session == IntPtr.Zero)
            {
                return (globalOverrides, profileOverrides, profileNameFound, $"NvAPI_DRS_CreateSession failed (rc={rc}).");
            }

            try
            {
                rc = loadSettings(session);
                if (rc != 0)
                {
                    return (globalOverrides, profileOverrides, profileNameFound, $"NvAPI_DRS_LoadSettings failed (rc={rc}).");
                }

                // 1. Scan BASE (Global) profile for DLSS / MFG / Streamline overrides
                if (getBase(session, out IntPtr baseProf) == 0 && baseProf != IntPtr.Zero)
                {
                    var found = ReadProfileDlssSettings(session, baseProf, isBaseProfile: true, getSetting, enumSettings, getSettingName);
                    globalOverrides.AddRange(found);
                }

                // 2. Find Crimson Desert profile
                IntPtr gameProf = IntPtr.Zero;
                foreach (string candidate in new[] { "Crimson Desert", "Crimson Desert Enhanced" })
                {
                    if (findProfile(session, candidate, out IntPtr p) == 0 && p != IntPtr.Zero)
                    {
                        gameProf = p;
                        profileNameFound = candidate;
                        break;
                    }
                }

                if (gameProf != IntPtr.Zero)
                {
                    var found = ReadProfileDlssSettings(session, gameProf, isBaseProfile: false, getSetting, enumSettings, getSettingName);
                    profileOverrides.AddRange(found);
                }
            }
            finally
            {
                destroySession(session);
            }

            return (globalOverrides, profileOverrides, profileNameFound, null);
        }
        catch (DllNotFoundException)
        {
            return (globalOverrides, profileOverrides, profileNameFound, "nvapi64.dll not found (NVIDIA driver not installed).");
        }
        catch (Exception ex)
        {
            return (globalOverrides, profileOverrides, profileNameFound, $"NVAPI DRS scan error: {ex.Message}");
        }
    }

    public (bool Success, List<DrsBackupEntry> BackedUp, List<string> Logs) ResetDrsSettings(
        bool isBaseProfile,
        string profileName,
        IEnumerable<uint> settingIds)
    {
        var logs = new List<string>();
        var backedUp = new List<DrsBackupEntry>();
        var ids = settingIds.Distinct().ToList();
        if (ids.Count == 0)
        {
            return (true, backedUp, logs);
        }

        try
        {
            var init = GetProc<NvApiInitializeDelegate>(FID_Initialize);
            var createSession = GetProc<NvApiDrsCreateSessionDelegate>(FID_DRS_CreateSession);
            var loadSettings = GetProc<NvApiDrsSessionOnlyDelegate>(FID_DRS_LoadSettings);
            var saveSettings = GetProc<NvApiDrsSessionOnlyDelegate>(FID_DRS_SaveSettings);
            var destroySession = GetProc<NvApiDrsDestroySessionDelegate>(FID_DRS_DestroySession);
            var getBase = GetProc<NvApiDrsGetBaseProfileDelegate>(FID_DRS_GetBaseProfile);
            var findProfile = GetProc<NvApiDrsFindProfileByNameDelegate>(FID_DRS_FindProfileByName);
            var getSetting = GetProc<NvApiDrsGetSettingDelegate>(FID_DRS_GetSetting);
            var deleteSetting = GetProcWithFallback<NvApiDrsSettingIdOpDelegate>(FID_DRS_DeleteProfileSetting_Ex, FID_DRS_DeleteProfileSetting);
            var restoreDefault = GetProc<NvApiDrsSettingIdOpDelegate>(FID_DRS_RestoreProfileDefaultSetting);
            var getSettingName = GetProcWithFallback<NvApiDrsGetSettingNameFromIdDelegate>(FID_DRS_GetSettingNameFromId_Ex, FID_DRS_GetSettingNameFromId);

            if (init is null || createSession is null || loadSettings is null || saveSettings is null ||
                destroySession is null || getBase is null || findProfile is null || getSetting is null)
            {
                logs.Add("ERROR: Required NVAPI DRS functions could not be resolved.");
                return (false, backedUp, logs);
            }

            if (init() != 0 || createSession(out IntPtr session) != 0 || loadSettings(session) != 0)
            {
                logs.Add("ERROR: Failed to initialize NVAPI DRS session.");
                return (false, backedUp, logs);
            }

            try
            {
                IntPtr prof = IntPtr.Zero;
                if (isBaseProfile)
                {
                    getBase(session, out prof);
                }
                else
                {
                    findProfile(session, profileName, out prof);
                }

                if (prof == IntPtr.Zero)
                {
                    logs.Add($"WARNING: NVIDIA DRS profile '{(isBaseProfile ? "BASE (Global)" : profileName)}' not found.");
                    return (false, backedUp, logs);
                }

                int changed = 0;
                foreach (uint sid in ids)
                {
                    // Snapshot current value for backup before deleting
                    var snap = ReadSingleSetting(session, prof, sid, isBaseProfile, getSetting, getSettingName);
                    if (snap is not null && snap.SettingLocation == 0 && !snap.IsCurrentPredefined)
                    {
                        backedUp.Add(new DrsBackupEntry
                        {
                            ProfileName = isBaseProfile ? "BASE" : profileName,
                            IsBaseProfile = isBaseProfile,
                            SettingId = sid,
                            SettingName = snap.SettingName,
                            SettingType = snap.SettingType,
                            U32Value = snap.CurrentU32,
                            StringValue = snap.SettingType is 2 or 3 ? snap.CurrentString : null
                        });
                    }

                    int delRc = deleteSetting is not null ? deleteSetting(session, prof, sid) : -1;
                    if (delRc == 0)
                    {
                        changed++;
                        logs.Add($"Deleted DRS override 0x{sid:X8} ({GetFriendlyName(sid, snap?.SettingName)}) from {(isBaseProfile ? "Global BASE" : profileName)} profile.");
                    }
                    else if (restoreDefault is not null && restoreDefault(session, prof, sid) == 0)
                    {
                        changed++;
                        logs.Add($"Restored DRS default 0x{sid:X8} ({GetFriendlyName(sid, snap?.SettingName)}) in {(isBaseProfile ? "Global BASE" : profileName)} profile.");
                    }
                    else
                    {
                        logs.Add($"Note: 0x{sid:X8} in {(isBaseProfile ? "Global BASE" : profileName)} was already at default or not present (rc={delRc}).");
                    }
                }

                if (changed > 0)
                {
                    int saveRc = saveSettings(session);
                    if (saveRc != 0)
                    {
                        logs.Add($"ERROR: NvAPI_DRS_SaveSettings failed (rc={saveRc}). Try running as Administrator if driver profile is locked.");
                        return (false, backedUp, logs);
                    }
                    logs.Add($"Saved NVIDIA DRS settings ({changed} setting(s) cleaned in {(isBaseProfile ? "Global BASE" : profileName)}).");
                }

                return (true, backedUp, logs);
            }
            finally
            {
                destroySession(session);
            }
        }
        catch (Exception ex)
        {
            logs.Add($"ERROR: NVAPI DRS reset exception: {ex.Message}");
            return (false, backedUp, logs);
        }
    }

    /// <summary>
    /// Reads raw 16-byte DRS records directly from the active nvdrsdb0/1.bin file.
    /// Necessary because public NvAPI_DRS_GetSetting / EnumSettings redact private settings
    /// such as 0xB0D384C0 (NVIDIA Smooth Motion / DriverFGX) and 0x00634291 / 0x00598928 (RTX HDR).
    /// </summary>
    public Dictionary<uint, RawDrsBinRecord> ReadProfileRecordsFromActiveDrsBin(string profileName)
    {
        var result = new Dictionary<uint, RawDrsBinRecord>();
        try
        {
            string drsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "NVIDIA Corporation", "Drs");
            string selPath = Path.Combine(drsDir, "nvdrssel.bin");
            int activeIdx = 0;
            if (File.Exists(selPath))
            {
                byte[] sel = File.ReadAllBytes(selPath);
                if (sel.Length > 0 && sel[0] == 1) activeIdx = 1;
            }

            string dbPath = Path.Combine(drsDir, activeIdx == 1 ? "nvdrsdb1.bin" : "nvdrsdb0.bin");
            if (!File.Exists(dbPath))
            {
                dbPath = Path.Combine(drsDir, "nvdrsdb0.bin");
                if (!File.Exists(dbPath)) return result;
            }

            byte[] data = File.ReadAllBytes(dbPath);
            byte[] needle = Encoding.Unicode.GetBytes(profileName + "\0");
            int pos = data.AsSpan().IndexOf(needle);
            if (pos < 16) return result;

            // Contiguous 16-byte records immediately precede the UTF-16LE profile name:
            // [a4 00 10 00] [u32 settingId] [u32 flags (0x1002=user, 0x1000=predef)] [u32 value]
            int off = pos - 16;
            while (off >= 0 &&
                   data[off] == 0xA4 && data[off + 1] == 0x00 &&
                   data[off + 2] == 0x10 && data[off + 3] == 0x00)
            {
                uint sid = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 4, 4));
                uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 8, 4));
                uint val = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 12, 4));
                result[sid] = new RawDrsBinRecord { SettingId = sid, Flags = flags, Value = val };
                off -= 16;
            }
        }
        catch
        {
            // Fall back gracefully if DRS bin cannot be read
        }
        return result;
    }

    public (bool Success, List<DrsBackupEntry> BackedUp, List<string> Logs) SetDrsSettingsU32(
        bool isBaseProfile,
        string profileName,
        IEnumerable<(uint SettingId, uint Value)> updates)
    {
        var logs = new List<string>();
        var backedUp = new List<DrsBackupEntry>();
        var list = updates.ToList();
        if (list.Count == 0) return (true, backedUp, logs);

        // Read existing bin records first so even private IDs (0xB0D384C0, 0x00634291, etc.) get exact backup state!
        var existingBin = ReadProfileRecordsFromActiveDrsBin(isBaseProfile ? "Base Profile" : profileName);

        try
        {
            var init = GetProc<NvApiInitializeDelegate>(FID_Initialize);
            var createSession = GetProc<NvApiDrsCreateSessionDelegate>(FID_DRS_CreateSession);
            var loadSettings = GetProc<NvApiDrsSessionOnlyDelegate>(FID_DRS_LoadSettings);
            var saveSettings = GetProc<NvApiDrsSessionOnlyDelegate>(FID_DRS_SaveSettings);
            var destroySession = GetProc<NvApiDrsDestroySessionDelegate>(FID_DRS_DestroySession);
            var getBase = GetProc<NvApiDrsGetBaseProfileDelegate>(FID_DRS_GetBaseProfile);
            var findProfile = GetProc<NvApiDrsFindProfileByNameDelegate>(FID_DRS_FindProfileByName);
            var setSettingEx = GetProc<NvApiDrsSetSettingExDelegate>(FID_DRS_SetSetting_Ex);
            var setSetting = GetProc<NvApiDrsSetSettingDelegate>(FID_DRS_SetSetting);

            if (init is null || createSession is null || loadSettings is null || saveSettings is null ||
                destroySession is null || getBase is null || findProfile is null || (setSettingEx is null && setSetting is null))
            {
                logs.Add("ERROR: Required NVAPI DRS functions unavailable.");
                return (false, backedUp, logs);
            }

            if (init() != 0 || createSession(out IntPtr session) != 0 || loadSettings(session) != 0)
            {
                logs.Add("ERROR: Failed to initialize NVAPI DRS session.");
                return (false, backedUp, logs);
            }

            try
            {
                IntPtr prof = IntPtr.Zero;
                if (isBaseProfile) getBase(session, out prof);
                else
                {
                    findProfile(session, profileName, out prof);
                    if (prof == IntPtr.Zero)
                    {
                        prof = TryCreateCrimsonDesertProfile(session, profileName, logs);
                    }
                }

                if (prof == IntPtr.Zero)
                {
                    logs.Add($"WARNING: NVIDIA DRS profile '{(isBaseProfile ? "Base Profile" : profileName)}' not found and could not be created.");
                    return (false, backedUp, logs);
                }

                int changed = 0;
                IntPtr buf = Marshal.AllocHGlobal(NVDRS_SETTING_SIZE);
                try
                {
                    foreach (var (sid, val) in list)
                    {
                        bool hadUserEntry = existingBin.TryGetValue(sid, out var prev) && prev.IsUserModified;
                        backedUp.Add(new DrsBackupEntry
                        {
                            ProfileName = isBaseProfile ? "BASE" : profileName,
                            IsBaseProfile = isBaseProfile,
                            SettingId = sid,
                            SettingName = GetFriendlyName(sid, null),
                            SettingType = 0,
                            U32Value = hadUserEntry ? prev!.Value : 0u,
                            WasAbsentBeforeFix = !hadUserEntry
                        });

                        ZeroMemory(buf, NVDRS_SETTING_SIZE);
                        Marshal.WriteInt32(buf, OFF_VERSION, unchecked((int)NVDRS_SETTING_VER));
                        Marshal.WriteInt32(buf, OFF_ID, unchecked((int)sid));
                        Marshal.WriteInt32(buf, OFF_TYPE, 0); // NVDRS_DWORD_TYPE
                        Marshal.WriteInt32(buf, OFF_CURR_VAL, unchecked((int)val));

                        int rc = setSettingEx is not null
                            ? setSettingEx(session, prof, buf, 0, 0)
                            : setSetting!(session, prof, buf);
                        if (rc == 0)
                        {
                            changed++;
                            logs.Add($"Set DRS 0x{sid:X8} ({GetFriendlyName(sid, null)}) = 0x{val:X} in {(isBaseProfile ? "Global Base Profile" : profileName)}.");
                        }
                        else
                        {
                            logs.Add($"WARNING: NvAPI_DRS_SetSetting(0x{sid:X8}=0x{val:X}) returned rc={rc}.");
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }

                if (changed > 0)
                {
                    int saveRc = saveSettings(session);
                    if (saveRc != 0)
                    {
                        logs.Add($"ERROR: NvAPI_DRS_SaveSettings failed (rc={saveRc}).");
                        return (false, backedUp, logs);
                    }
                    logs.Add($"Saved NVIDIA DRS settings ({changed} setting(s) updated in {(isBaseProfile ? "Global Base Profile" : profileName)}).");
                }

                return (changed > 0, backedUp, logs);
            }
            finally
            {
                destroySession(session);
            }
        }
        catch (Exception ex)
        {
            logs.Add($"ERROR: NVAPI DRS set exception: {ex.Message}");
            return (false, backedUp, logs);
        }
    }

    public (bool Success, List<string> Logs) RestoreDrsBackup(IEnumerable<DrsBackupEntry> entries)
    {
        var logs = new List<string>();
        var list = entries.ToList();
        if (list.Count == 0)
        {
            return (true, logs);
        }

        try
        {
            var init = GetProc<NvApiInitializeDelegate>(FID_Initialize);
            var createSession = GetProc<NvApiDrsCreateSessionDelegate>(FID_DRS_CreateSession);
            var loadSettings = GetProc<NvApiDrsSessionOnlyDelegate>(FID_DRS_LoadSettings);
            var saveSettings = GetProc<NvApiDrsSessionOnlyDelegate>(FID_DRS_SaveSettings);
            var destroySession = GetProc<NvApiDrsDestroySessionDelegate>(FID_DRS_DestroySession);
            var getBase = GetProc<NvApiDrsGetBaseProfileDelegate>(FID_DRS_GetBaseProfile);
            var findProfile = GetProc<NvApiDrsFindProfileByNameDelegate>(FID_DRS_FindProfileByName);
            var setSettingEx = GetProc<NvApiDrsSetSettingExDelegate>(FID_DRS_SetSetting_Ex);
            var setSetting = GetProc<NvApiDrsSetSettingDelegate>(FID_DRS_SetSetting);
            var deleteSetting = GetProcWithFallback<NvApiDrsSettingIdOpDelegate>(FID_DRS_DeleteProfileSetting_Ex, FID_DRS_DeleteProfileSetting);

            if (init is null || createSession is null || loadSettings is null || saveSettings is null ||
                destroySession is null || getBase is null || findProfile is null || (setSettingEx is null && setSetting is null))
            {
                logs.Add("ERROR: Required NVAPI DRS functions unavailable for restore.");
                return (false, logs);
            }

            if (init() != 0 || createSession(out IntPtr session) != 0 || loadSettings(session) != 0)
            {
                logs.Add("ERROR: Failed to initialize NVAPI DRS session for restore.");
                return (false, logs);
            }

            try
            {
                IntPtr buf = Marshal.AllocHGlobal(NVDRS_SETTING_SIZE);
                try
                {
                    foreach (var entry in list)
                    {
                        IntPtr prof = IntPtr.Zero;
                        if (entry.IsBaseProfile)
                            getBase(session, out prof);
                        else
                            findProfile(session, entry.ProfileName, out prof);

                        if (prof == IntPtr.Zero) continue;

                        if (entry.WasAbsentBeforeFix && deleteSetting is not null)
                        {
                            int drc = deleteSetting(session, prof, entry.SettingId);
                            logs.Add(drc == 0
                                ? $"Removed added DRS override 0x{entry.SettingId:X8} from {(entry.IsBaseProfile ? "Global BASE" : entry.ProfileName)}."
                                : $"Note: Delete 0x{entry.SettingId:X8} returned rc={drc}.");
                            continue;
                        }

                        ZeroMemory(buf, NVDRS_SETTING_SIZE);
                        Marshal.WriteInt32(buf, OFF_VERSION, unchecked((int)NVDRS_SETTING_VER));
                        Marshal.WriteInt32(buf, OFF_ID, unchecked((int)entry.SettingId));
                        Marshal.WriteInt32(buf, OFF_TYPE, unchecked((int)entry.SettingType));

                        if (entry.SettingType == 0)
                        {
                            Marshal.WriteInt32(buf, OFF_CURR_VAL, unchecked((int)entry.U32Value));
                        }
                        else if (entry.SettingType is 2 or 3 && entry.StringValue is not null)
                        {
                            byte[] bytes = Encoding.Unicode.GetBytes(entry.StringValue + "\0");
                            Marshal.Copy(bytes, 0, IntPtr.Add(buf, OFF_CURR_VAL), Math.Min(bytes.Length, 4096));
                        }

                        int rc = setSettingEx is not null
                            ? setSettingEx(session, prof, buf, 0, 0)
                            : setSetting!(session, prof, buf);
                        logs.Add(rc == 0
                            ? $"Restored DRS 0x{entry.SettingId:X8}=0x{entry.U32Value:X} in {(entry.IsBaseProfile ? "Global BASE" : entry.ProfileName)}."
                            : $"WARNING: Failed to restore DRS 0x{entry.SettingId:X8} (rc={rc}).");
                    }

                    int saveRc = saveSettings(session);
                    logs.Add(saveRc == 0
                        ? "Saved restored NVIDIA DRS settings."
                        : $"ERROR: Failed saving restored DRS settings (rc={saveRc}).");
                    return (saveRc == 0, logs);
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            finally
            {
                destroySession(session);
            }
        }
        catch (Exception ex)
        {
            logs.Add($"ERROR restoring DRS backup: {ex.Message}");
            return (false, logs);
        }
    }

    private static List<DrsSettingSnapshot> ReadProfileDlssSettings(
        IntPtr session,
        IntPtr profile,
        bool isBaseProfile,
        NvApiDrsGetSettingDelegate getSetting,
        NvApiDrsEnumSettingsDelegate enumSettings,
        NvApiDrsGetSettingNameFromIdDelegate? getSettingName)
    {
        var byId = new Dictionary<uint, DrsSettingSnapshot>();

        // Direct query of all known DLSS/MFG/Streamline IDs (catches settings EnumSettings may omit)
        foreach (uint sid in KnownDlssMfgIds.Keys)
        {
            var snap = ReadSingleSetting(session, profile, sid, isBaseProfile, getSetting, getSettingName);
            if (snap is null) continue;

            // Only include settings actually set on THIS profile (settingLocation == 0) and user-modified (!IsCurrentPredefined)
            if (snap.SettingLocation == 0 && !snap.IsCurrentPredefined)
            {
                byId[sid] = snap;
            }
        }

        // Also enumerate all settings in the profile to catch any unknown DLSS/NGX/Streamline/MFG overrides
        IntPtr buf = Marshal.AllocHGlobal(NVDRS_SETTING_SIZE);
        try
        {
            uint index = 0;
            while (index < 512)
            {
                ZeroMemory(buf, NVDRS_SETTING_SIZE);
                Marshal.WriteInt32(buf, OFF_VERSION, unchecked((int)NVDRS_SETTING_VER));
                uint count = 1;
                int rc = enumSettings(session, profile, index, ref count, buf);
                if (rc != 0 || count == 0) break;
                index++;

                var snap = ParseSettingBuffer(buf, isBaseProfile, getSettingName);
                if (snap.SettingLocation != 0 || snap.IsCurrentPredefined) continue;

                bool isDlssOrMfg = KnownDlssMfgIds.ContainsKey(snap.SettingId)
                    || (snap.SettingId >= 0x10E40000 && snap.SettingId <= 0x10E4FFFF)
                    || snap.SettingName.Contains("DLSS", StringComparison.OrdinalIgnoreCase)
                    || snap.SettingName.Contains("NGX", StringComparison.OrdinalIgnoreCase)
                    || snap.SettingName.Contains("Streamline", StringComparison.OrdinalIgnoreCase)
                    || snap.SettingName.Contains("multi-frame", StringComparison.OrdinalIgnoreCase);

                if (isDlssOrMfg && !byId.ContainsKey(snap.SettingId))
                {
                    byId[snap.SettingId] = snap;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }

        return byId.Values.OrderBy(x => x.SettingId).ToList();
    }

    private static DrsSettingSnapshot? ReadSingleSetting(
        IntPtr session,
        IntPtr profile,
        uint settingId,
        bool isBaseProfile,
        NvApiDrsGetSettingDelegate getSetting,
        NvApiDrsGetSettingNameFromIdDelegate? getSettingName)
    {
        IntPtr buf = Marshal.AllocHGlobal(NVDRS_SETTING_SIZE);
        try
        {
            ZeroMemory(buf, NVDRS_SETTING_SIZE);
            Marshal.WriteInt32(buf, OFF_VERSION, unchecked((int)NVDRS_SETTING_VER));
            int rc = getSetting(session, profile, settingId, buf);
            if (rc != 0) return null;
            return ParseSettingBuffer(buf, isBaseProfile, getSettingName);
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private static DrsSettingSnapshot ParseSettingBuffer(
        IntPtr buf,
        bool isBaseProfile,
        NvApiDrsGetSettingNameFromIdDelegate? getSettingName)
    {
        uint id = unchecked((uint)Marshal.ReadInt32(buf, OFF_ID));
        uint type = unchecked((uint)Marshal.ReadInt32(buf, OFF_TYPE));
        uint loc = unchecked((uint)Marshal.ReadInt32(buf, OFF_LOCATION));
        bool isPredef = Marshal.ReadInt32(buf, OFF_IS_PREDEF) != 0;
        bool isPredefValid = Marshal.ReadInt32(buf, OFF_IS_PREDEF_VALID) != 0;
        uint predefU32 = unchecked((uint)Marshal.ReadInt32(buf, OFF_PREDEF_VAL));
        uint currU32 = unchecked((uint)Marshal.ReadInt32(buf, OFF_CURR_VAL));

        string name = Marshal.PtrToStringUni(IntPtr.Add(buf, OFF_NAME)) ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name) && getSettingName is not null)
        {
            IntPtr nameBuf = Marshal.AllocHGlobal(4096);
            try
            {
                ZeroMemory(nameBuf, 4096);
                if (getSettingName(id, nameBuf) == 0)
                {
                    name = Marshal.PtrToStringUni(nameBuf) ?? string.Empty;
                }
            }
            finally
            {
                Marshal.FreeHGlobal(nameBuf);
            }
        }

        string currStr = type is 2 or 3 ? (Marshal.PtrToStringUni(IntPtr.Add(buf, OFF_CURR_VAL)) ?? string.Empty) : string.Empty;
        string predefStr = type is 2 or 3 ? (Marshal.PtrToStringUni(IntPtr.Add(buf, OFF_PREDEF_VAL)) ?? string.Empty) : string.Empty;

        return new DrsSettingSnapshot
        {
            SettingId = id,
            SettingName = name,
            FriendlyName = GetFriendlyName(id, name),
            SettingType = type,
            SettingLocation = loc,
            IsCurrentPredefined = isPredef,
            IsPredefinedValid = isPredefValid,
            CurrentU32 = currU32,
            PredefinedU32 = predefU32,
            CurrentString = currStr,
            PredefinedString = predefStr,
            IsFromBaseProfile = isBaseProfile,
            DecodedCurrentValue = DecodeValue(id, type, currU32, currStr)
        };
    }

    public static string GetFriendlyName(uint settingId, string? nvapiName)
    {
        if (!string.IsNullOrWhiteSpace(nvapiName)) return nvapiName;
        if (KnownDlssMfgIds.TryGetValue(settingId, out var info)) return info.Name;
        if (AdditionalFriendlyNames.TryGetValue(settingId, out var extra)) return extra;
        return $"Unknown DRS Setting (0x{settingId:X8})";
    }

    public static string DecodeValue(uint settingId, uint settingType, uint u32Val, string strVal)
    {
        if (settingType is 2 or 3) return $"\"{strVal}\"";
        return settingId switch
        {
            0x10E41E06 => u32Val == 1 ? "0x1 (ENABLED — Forces OTA Streamline override)" : $"0x{u32Val:X}",
            0x10308298 => u32Val switch
            {
                0 => "0x0 (Default / Off)",
                1 => "0x1 (App Controlled / Override)",
                2 => "0x2 (FORCED FIXED MFG / DLSSG MODE)",
                4 => "0x4 (FORCED DYNAMIC MFG MODE)",
                _ => $"0x{u32Val:X} (Forced DLSSG/MFG mode)"
            },
            0x104D6667 => u32Val switch
            {
                0 => "0x0 (Zeroed/Dirty — conflicts if DLSSG mode is forced!)",
                1 => "0x1 (2x Frame Gen)",
                2 => "0x2 (3x MFG)",
                3 => "0x3 (4x MFG)",
                4 => "0x4 (5x MFG)",
                5 => "0x5 (6x MFG)",
                _ => $"0x{u32Val:X}"
            },
            0x10AFB768 or 0x10BD9423 => u32Val switch
            {
                0 => "0x0 (Default / App Controlled)",
                1 => "0x1 (Forced Performance)",
                2 => "0x2 (Forced Balanced)",
                3 => "0x3 (FORCED QUALITY / ULTRA-PERF MODE OVERRIDE)",
                4 => "0x4 (Forced Ultra Performance)",
                5 => "0x5 (Forced DLAA)",
                6 => "0x6 (Forced Custom)",
                _ => $"0x{u32Val:X} (Forced mode override)"
            },
            0x10E41E01 or 0x10E41E02 or 0x10E41E03 or 0x10E41E04 =>
                u32Val == 0 ? "0x0 (Dirty user override entry)" : $"0x{u32Val:X} (ENABLED override)",
            _ => $"0x{u32Val:X}"
        };
    }

    private static IntPtr TryCreateCrimsonDesertProfile(IntPtr session, string profileName, List<string> logs)
    {
        var createProfile = GetProc<NvApiDrsCreateProfileDelegate>(FID_DRS_CreateProfile);
        var createApp = GetProc<NvApiDrsCreateApplicationDelegate>(FID_DRS_CreateApplication);
        if (createProfile is null) return IntPtr.Zero;

        // NVDRS_PROFILE_V1: version(4) + profileName(2048*2=4096) + gpuSupport(4) + isPredefined(4) + numOfApps(4) + numOfSettings(4) = 4116 bytes
        const int PROF_SIZE = 4116;
        uint profVer = PROF_SIZE | (1u << 16);
        IntPtr pProf = Marshal.AllocHGlobal(PROF_SIZE);
        try
        {
            ZeroMemory(pProf, PROF_SIZE);
            Marshal.WriteInt32(pProf, 0, unchecked((int)profVer));
            byte[] nameBytes = Encoding.Unicode.GetBytes(profileName + "\0");
            Marshal.Copy(nameBytes, 0, IntPtr.Add(pProf, 4), Math.Min(nameBytes.Length, 4096));

            if (createProfile(session, pProf, out IntPtr newProf) != 0 || newProf == IntPtr.Zero)
                return IntPtr.Zero;

            logs.Add($"Created missing NVIDIA DRS profile '{profileName}' (for older drivers without a predefined profile).");

            if (createApp is not null)
            {
                // NVDRS_APPLICATION_V1: version(4) + isPredefined(4) + appName(4096) + userFriendlyName(4096) + launcher(4096) = 12296 bytes
                const int APP_SIZE = 12296;
                uint appVer = APP_SIZE | (1u << 16);
                IntPtr pApp = Marshal.AllocHGlobal(APP_SIZE);
                try
                {
                    ZeroMemory(pApp, APP_SIZE);
                    Marshal.WriteInt32(pApp, 0, unchecked((int)appVer));
                    byte[] exeBytes = Encoding.Unicode.GetBytes("crimsondesert.exe\0");
                    Marshal.Copy(exeBytes, 0, IntPtr.Add(pApp, 8), Math.Min(exeBytes.Length, 4096));
                    byte[] friendlyBytes = Encoding.Unicode.GetBytes("Crimson Desert\0");
                    Marshal.Copy(friendlyBytes, 0, IntPtr.Add(pApp, 8 + 4096), Math.Min(friendlyBytes.Length, 4096));
                    createApp(session, newProf, pApp);
                }
                finally
                {
                    Marshal.FreeHGlobal(pApp);
                }
            }

            return newProf;
        }
        finally
        {
            Marshal.FreeHGlobal(pProf);
        }
    }

    private static void ZeroMemory(IntPtr ptr, int length)
    {
        // Fast block zeroing
        byte[] zeros = new byte[Math.Min(length, 4096)];
        int offset = 0;
        while (offset < length)
        {
            int chunk = Math.Min(zeros.Length, length - offset);
            Marshal.Copy(zeros, 0, IntPtr.Add(ptr, offset), chunk);
            offset += chunk;
        }
    }
}
