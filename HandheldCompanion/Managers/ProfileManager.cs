﻿using HandheldCompanion.Controllers;
using HandheldCompanion.Controls;
using HandheldCompanion.Misc;
using HandheldCompanion.Utils;
using HandheldCompanion.Views;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using static HandheldCompanion.Utils.XInputPlusUtils;

namespace HandheldCompanion.Managers;

public static class ProfileManager
{
    public const string DefaultName = "Default";

    public static Dictionary<string, Profile> profiles = new(StringComparer.InvariantCultureIgnoreCase);
    public static List<Profile> subProfiles = new();

    private static Profile currentProfile;

    private static string ProfilesPath;

    public static bool IsInitialized;

    static ProfileManager()
    {
        // initialiaze path(s)
        ProfilesPath = Path.Combine(MainWindow.SettingsPath, "profiles");
        if (!Directory.Exists(ProfilesPath))
            Directory.CreateDirectory(ProfilesPath);

        ProcessManager.ForegroundChanged += ProcessManager_ForegroundChanged;
        ProcessManager.ProcessStarted += ProcessManager_ProcessStarted;
        ProcessManager.ProcessStopped += ProcessManager_ProcessStopped;

        PowerProfileManager.Deleted += PowerProfileManager_Deleted;

        ControllerManager.ControllerPlugged += ControllerManager_ControllerPlugged;
    }

    public static FileSystemWatcher profileWatcher { get; set; }

    public static void Start()
    {
        // monitor profile files
        profileWatcher = new FileSystemWatcher
        {
            Path = ProfilesPath,
            EnableRaisingEvents = true,
            IncludeSubdirectories = true,
            Filter = "*.json",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size
        };
        profileWatcher.Deleted += ProfileDeleted;

        // process existing profiles
        var fileEntries = Directory.GetFiles(ProfilesPath, "*.json", SearchOption.AllDirectories);
        foreach (var fileName in fileEntries)
            ProcessProfile(fileName);

        // check for default profile
        if (!HasDefault())
        {
            Layout deviceLayout = MainWindow.CurrentDevice.DefaultLayout.Clone() as Layout;
            Profile defaultProfile = new()
            {
                Name = DefaultName,
                Default = true,
                Enabled = false,
                Layout = deviceLayout,
                LayoutTitle = LayoutTemplate.DefaultLayout.Name,
                LayoutEnabled = true
            };

            UpdateOrCreateProfile(defaultProfile, UpdateSource.Creation);
        }

        IsInitialized = true;
        Initialized?.Invoke();

        LogManager.LogInformation("{0} has started", "ProfileManager");
    }

    public static void Stop()
    {
        if (!IsInitialized)
            return;

        IsInitialized = false;

        profileWatcher.Deleted -= ProfileDeleted;
        profileWatcher.Dispose();

        LogManager.LogInformation("{0} has stopped", "ProfileManager");
    }

    public static bool Contains(Profile profile)
    {
        foreach (var pr in profiles.Values)
            if (pr.Path.Equals(profile.Path, StringComparison.InvariantCultureIgnoreCase))
                return true;

        return false;
    }

    public static bool Contains(string fileName)
    {
        foreach (var pr in profiles.Values)
            if (pr.Path.Equals(fileName, StringComparison.InvariantCultureIgnoreCase))
                return true;

        return false;
    }

    public static Profile GetProfileFromPath(string path, bool ignoreStatus)
    {
        // check if favorite sub profile exists for path
        Profile profile = subProfiles.FirstOrDefault(pr => pr.Path == path && pr.IsFavoriteSubProfile);

        // get main profile from path instead
        if (profile is null)
            profile = profiles.Values.FirstOrDefault(a => a.Path.Equals(path, StringComparison.InvariantCultureIgnoreCase));

        if (profile is null)
        {
            // otherwise, get profile from executable
            string fileName = Path.GetFileName(path);
            profile = profiles.Values.FirstOrDefault(a => a.Executable.Equals(fileName, StringComparison.InvariantCultureIgnoreCase));

            if (profile is null)
                return GetDefault();
        }

        // ignore profile status (enabled/disabled)
        if (ignoreStatus)
            return profile;

        return profile.Enabled ? profile : GetDefault();
    }

    public static Profile GetProfileFromGuid(Guid Guid, bool ignoreStatus, bool isSubProfile = false)
    {
        Profile profile = null;

        if (isSubProfile)
            profile = subProfiles.FirstOrDefault(pr => pr.Guid == Guid);
        else
            profile = profiles.Values.FirstOrDefault(pr => pr.Guid == Guid);

        // get profile from path
        if (profile is null)
            return GetDefault();

        // ignore profile status (enabled/disabled)
        if (ignoreStatus)
            return profile;

        return profile.Enabled ? profile : GetDefault();
    }

    public static Profile[] GetSubProfilesFromPath(string path, bool ignoreStatus)
    {
        // get subprofile corresponding to path
        List<Profile> filteredSubProfiles = subProfiles.Where(pr => pr.Path == path).ToList();
        return filteredSubProfiles.OrderBy(pr => pr.Name).ToArray();
    }

    public static Profile GetProfileForSubProfile(Profile subProfile)
    {
        // if passed in profile is main profile
        if (!subProfile.IsSubProfile || !profiles.ContainsKey(subProfile.Path))
            return subProfile;

        // get the main profile if it exists/loaded .. else return the profile itself
        return profiles[subProfile.Path];
    }

    public static void SetSubProfileAsFavorite(Profile subProfile)
    {
        // remove favorite from all subprofiles
        foreach (var profile in GetSubProfilesFromPath(subProfile.Path, false))
        {
            profile.IsFavoriteSubProfile = false;
            SerializeProfile(profile);
        }

        // check if subProfile is not the main profile itself
        if (subProfile.IsSubProfile)
        {
            subProfile.IsFavoriteSubProfile = true;
            SerializeProfile(subProfile);
        }
    }

    public static void CycleSubProfiles(bool previous = false)
    {
        if (currentProfile == null)
            return;
        // called using previousSubProfile/nextSubProfile hotkeys
        List<Profile> subProfilesList = new();
        subProfilesList.Add(GetProfileForSubProfile(currentProfile)); // adds main profile as sub profile
        subProfilesList.AddRange(GetSubProfilesFromPath(currentProfile.Path, false).ToList()); // adds all sub profiles

        // if profile does not have sub profiles -> do nothing
        if (subProfilesList.Count <= 1)
            return;

        // get index of currently applied profile
        int currentIndex = subProfilesList.IndexOf(currentProfile);        
        int newIndex = currentIndex;

        // previous? decrement, next? increment
        if (previous)
            newIndex -= 1;
        else
            newIndex += 1;

        // ensure index is within list bounds, wrap if needed
        newIndex = (newIndex + subProfilesList.Count) % subProfilesList.Count;

        // if for whatever reason index is out of bound -> return
        if (newIndex < 0 || newIndex >= subProfilesList.Count)
            return;

        // apply profile
        Profile profileToApply = subProfilesList[newIndex];
        UpdateOrCreateProfile(profileToApply);
    }


    private static void ApplyProfile(Profile profile, UpdateSource source = UpdateSource.Background,
        bool announce = true)
    {
        // might not be the same anymore if disabled
        profile = GetProfileFromGuid(profile.Guid, false, profile.IsSubProfile);

        // we've already announced this profile
        if (currentProfile is not null)
            if (currentProfile.Guid == profile.Guid)
                announce = false;

        // update current profile before invoking event
        currentProfile = profile;

        // raise event
        Applied?.Invoke(profile, source);

        // send toast
        // todo: localize me
        if (announce)
        {
            LogManager.LogInformation("Profile {0} applied", profile.Name);
            ToastManager.SendToast($"Profile {profile.Name} applied");
        }
    }

    private static void PowerProfileManager_Deleted(PowerProfile powerProfile)
    {
        Profile profileToApply = null;

        // update main profiles
        foreach (Profile profile in profiles.Values)
        {
            bool isCurrent = profile.PowerProfile == powerProfile.Guid;
            if (isCurrent)
            {
                // sanitize profile
                SanitizeProfile(profile);

                // update profile
                UpdateOrCreateProfile(profile);

                if (currentProfile.Path.Equals(profile.Path, StringComparison.InvariantCultureIgnoreCase))
                    profileToApply = profile;
            }
        }

        // update sub profiles
        foreach (Profile profile in subProfiles)
        {
            bool isCurrent = profile.PowerProfile == powerProfile.Guid;
            if (isCurrent)
            {
                // sanitize profile
                SanitizeProfile(profile);

                // update profile
                UpdateOrCreateProfile(profile);

                if (currentProfile.Guid == profile.Guid)
                {
                    profileToApply = profile;
                }
            }
        }

        if (profileToApply != null)
            ApplyProfile(profileToApply);

    }

    private static void ProcessManager_ProcessStopped(ProcessEx processEx)
    {
        try
        {
            var profile = GetProfileFromPath(processEx.Path, true);

            // do not discard default profile
            if (profile is null || profile.Default)
                return;

            // raise event
            Discarded?.Invoke(profile);

            if (profile.ErrorCode.HasFlag(ProfileErrorCode.Running))
            {
                // update profile
                UpdateOrCreateProfile(profile);

                // restore default profile
                ApplyProfile(GetDefault());
            }
        }
        catch
        {
        }
    }

    private static void ProcessManager_ProcessStarted(ProcessEx processEx, bool OnStartup)
    {
        try
        {
            var profile = GetProfileFromPath(processEx.Path, true);

            if (profile is null || profile.Default)
                return;

            // update profile executable path
            profile.Path = processEx.Path;

            // update profile
            UpdateOrCreateProfile(profile);
        }
        catch
        {
        }
    }

    private static void ProcessManager_ForegroundChanged(ProcessEx proc, ProcessEx back)
    {
        try
        {
            var profile = GetProfileFromPath(proc.Path, false);

            // update profile executable path
            if (!profile.Default)
            {
                profile.Path = proc.Path;
                UpdateOrCreateProfile(profile);
            }

            // raise event
            if (back is not null)
            {
                var backProfile = GetProfileFromPath(back.Path, false);

                if (backProfile != profile)
                    Discarded?.Invoke(backProfile);
            }

            ApplyProfile(profile);
        }
        catch
        {
        }
    }

    private static void ProfileDeleted(object sender, FileSystemEventArgs e)
    {
        // not ideal
        var ProfileName = e.Name.Replace(".json", "");
        var profile = profiles.Values.FirstOrDefault(p => p.Name.Equals(ProfileName, StringComparison.InvariantCultureIgnoreCase));

        // couldn't find a matching profile
        if (profile is null)
            return;

        // you can't delete default profile !
        if (profile.Default)
        {
            SerializeProfile(profile);
            return;
        }

        DeleteProfile(profile);
    }

    private static bool HasDefault()
    {
        return profiles.Values.Count(a => a.Default) != 0;
    }

    public static Profile GetDefault()
    {
        if (HasDefault())
            return profiles.Values.FirstOrDefault(a => a.Default);
        return new Profile();
    }

    public static Profile GetCurrent()
    {
        if (currentProfile is not null)
            return currentProfile;

        return GetDefault();
    }

    private static void ProcessProfile(string fileName)
    {
        Profile profile = null;
        try
        {
            var outputraw = File.ReadAllText(fileName);
            var jObject = JObject.Parse(outputraw);

            // latest pre-versionning release
            Version version = new("0.15.0.4");
            if (jObject.TryGetValue("Version", out var value))
                version = new Version(value.ToString());

            switch (version.ToString())
            {
                case "0.15.0.4":
                    {
                        outputraw = CommonUtils.RegexReplace(outputraw, "Generic.Dictionary(.*)System.Private.CoreLib\"",
                            "Generic.SortedDictionary$1System.Collections\"");
                        jObject = JObject.Parse(outputraw);
                        jObject.Remove("MotionSensivityArray");
                        outputraw = jObject.ToString();
                    }
                    break;
                case "0.16.0.5":
                    {
                        outputraw = outputraw.Replace(
                            "\"System.Collections.Generic.SortedDictionary`2[[HandheldCompanion.Inputs.ButtonFlags, HandheldCompanion],[System.Boolean, System.Private.CoreLib]], System.Collections\"",
                            "\"System.Collections.Concurrent.ConcurrentDictionary`2[[HandheldCompanion.Inputs.ButtonFlags, HandheldCompanion],[System.Boolean, System.Private.CoreLib]], System.Collections.Concurrent\"");
                    }
                    break;
            }

            profile = JsonConvert.DeserializeObject<Profile>(outputraw, new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All
            });
        }
        catch (Exception ex)
        {
            LogManager.LogError("Could not parse profile {0}. {1}", fileName, ex.Message);
        }

        // failed to parse
        if (profile is null || profile.Name is null || profile.Path is null)
        {
            LogManager.LogError("Failed to parse profile {0}", fileName);
            return;
        }

        UpdateOrCreateProfile(profile, UpdateSource.Serializer);

        // default specific
        if (profile.Default)
            ApplyProfile(profile, UpdateSource.Serializer);
    }

    public static void DeleteProfile(Profile profile)
    {
        var profilePath = Path.Combine(ProfilesPath, profile.GetFileName());

        if (profiles.ContainsKey(profile.Path))
        {
            // delete associated subprofiles
            foreach (Profile subprofile in GetSubProfilesFromPath(profile.Path, false))
            {
                DeleteSubProfile(subprofile);
            }
            LogManager.LogInformation("Deleted subprofiles for profile {0}", profile);

            // Unregister application from HidHide
            HidHide.UnregisterApplication(profile.Path);

            // Remove XInputPlus (extended compatibility)
            XInputPlus.UnregisterApplication(profile);

            profiles.Remove(profile.Path);

            // warn owner
            var isCurrent = false;

            if (currentProfile != null)
                isCurrent = profile.Path.Equals(currentProfile.Path, StringComparison.InvariantCultureIgnoreCase);

            // raise event
            Discarded?.Invoke(profile);

            // raise event(s)
            Deleted?.Invoke(profile);

            // send toast
            // todo: localize me
            ToastManager.SendToast($"Profile {profile.Name} deleted");

            LogManager.LogInformation("Deleted profile {0}", profilePath);

            // restore default profile
            if (isCurrent)
                ApplyProfile(GetDefault());
        }

        FileUtils.FileDelete(profilePath);
    }

    public static void DeleteSubProfile(Profile subProfile)
    {
        var profilePath = Path.Combine(ProfilesPath, subProfile.GetFileName());

        if (subProfiles.Contains(subProfile))
        {
            // remove sub profile from memory
            subProfiles.Remove(subProfile);

            // warn owner
            var isCurrent = subProfile.Guid == currentProfile.Guid;

            // raise event
            Discarded?.Invoke(subProfile);

            // raise event(s)
            Deleted?.Invoke(subProfile);

            // send toast
            // todo: localize me
            ToastManager.SendToast($"Subprofile {subProfile.Name} deleted");

            LogManager.LogInformation("Deleted subprofile {0}", profilePath);

            // restore main profile as favorite
            if (isCurrent)
            {
                // apply the main profile if it still exists
                Profile originalProfile = profiles.Values.FirstOrDefault(p => p.Path == subProfile.Path, GetDefault());
                ApplyProfile(originalProfile);
            }
        }

        FileUtils.FileDelete(profilePath);
    }

    public static void SerializeProfile(Profile profile)
    {
        // update profile version to current build
        profile.Version = new Version(MainWindow.fileVersionInfo.FileVersion);

        var jsonString = JsonConvert.SerializeObject(profile, Formatting.Indented, new JsonSerializerSettings
        {
            TypeNameHandling = TypeNameHandling.All
        });

        // prepare for writing
        var profilePath = Path.Combine(ProfilesPath, profile.GetFileName());

        try
        {
            if (FileUtils.IsFileWritable(profilePath))
                File.WriteAllText(profilePath, jsonString);
        }
        catch { }
    }

    private static void SanitizeProfile(Profile profile)
    {
        profile.ErrorCode = ProfileErrorCode.None;

        if (profile.Default)
        {
            profile.ErrorCode |= ProfileErrorCode.Default;
        }
        else
        {
            var processpath = Path.GetDirectoryName(profile.Path);

            if (!Directory.Exists(processpath))
                profile.ErrorCode |= ProfileErrorCode.MissingPath;

            if (!File.Exists(profile.Path))
                profile.ErrorCode |= ProfileErrorCode.MissingExecutable;

            if (!FileUtils.IsDirectoryWritable(processpath))
                profile.ErrorCode |= ProfileErrorCode.MissingPermission;

            if (ProcessManager.GetProcesses(profile.Executable).Capacity > 0)
                profile.ErrorCode |= ProfileErrorCode.Running;
        }

        // looks like profile power profile was deleted, restore balanced
        if (!PowerProfileManager.Contains(profile.PowerProfile))
            profile.PowerProfile = OSPowerMode.BetterPerformance;
    }

    public static void UpdateOrCreateProfile(Profile profile, UpdateSource source = UpdateSource.Background)
    {
        LogManager.LogInformation($"Attempting to update/create profile {profile.Name} => sub profile? {profile.IsSubProfile}");
        bool isCurrent = false;
        switch (source)
        {
            // if profile is created from QT -> apply it
            case UpdateSource.QuickProfilesCreation:
                isCurrent = true;
                break;
            case UpdateSource.ProfilesPageUpdateOnly: // when renaming main profile in ProfilesPage
                isCurrent = false;
                break;
            default:
                // check if this is current profile
                isCurrent = currentProfile is null ? false : profile.Path.Equals(currentProfile.Path, StringComparison.InvariantCultureIgnoreCase);
                break;
        }

        // refresh error code
        SanitizeProfile(profile);

        // used to get and store a few previous values
        XInputPlusMethod prevWrapper = XInputPlusMethod.Disabled;
        if (!profile.IsSubProfile && profiles.TryGetValue(profile.Path, out Profile prevProfile))
        {
            prevWrapper = prevProfile.XInputPlus;
        }
        else if (profile.IsSubProfile) // TODO check if necessary
        {
            Profile prevSubProfile = subProfiles.FirstOrDefault(sub => sub.Guid == profile.Guid);
            if (prevSubProfile != null)
                prevWrapper = prevSubProfile.XInputPlus;
        }

        // update database
        if (profile.IsSubProfile)
        {
            // remove sub profile if it already exists, then add the updated one
            subProfiles = subProfiles.Where(pr => pr.Guid != profile.Guid).ToList();
            subProfiles.Add(profile);
        }
        else
            profiles[profile.Path] = profile;

        // raise event(s)
        Updated?.Invoke(profile, source, isCurrent);

        if (source == UpdateSource.Serializer)
            return;

        // do not update wrapper and cloaking from default profile
        if (!profile.Default)
        {
            // update wrapper
            if (!UpdateProfileWrapper(profile))
            {
                // restore previous XInputPlus mode if failed to update
                profile.XInputPlus = prevWrapper;
                source = UpdateSource.Background;
            }

            // update cloaking
            UpdateProfileCloaking(profile);
        }

        // apply profile (silently)
        LogManager.LogInformation($"Checking if profile: {profile} is current => {isCurrent}");
        if (isCurrent)
        {
            SetSubProfileAsFavorite(profile); // if sub profile, set it as favorite for main profile
            ApplyProfile(profile, source);
        }

        // serialize profile
        SerializeProfile(profile);
    }

    public static bool UpdateProfileCloaking(Profile profile)
    {
        switch (profile.ErrorCode)
        {
            case ProfileErrorCode.MissingExecutable:
            case ProfileErrorCode.MissingPath:
            case ProfileErrorCode.Default:
                return false;
        }

        switch (profile.Whitelisted)
        {
            case true:
                return HidHide.RegisterApplication(profile.Path);
            default:
            case false:
                return HidHide.UnregisterApplication(profile.Path);
        }
    }

    public static bool UpdateProfileWrapper(Profile profile)
    {
        switch (profile.ErrorCode)
        {
            case ProfileErrorCode.MissingPermission:
            case ProfileErrorCode.MissingPath:
            case ProfileErrorCode.Running:
            case ProfileErrorCode.Default:
                return false;
        }

        switch (profile.XInputPlus)
        {
            case XInputPlusMethod.Redirection:
                return XInputPlus.RegisterApplication(profile);
            default:
            case XInputPlusMethod.Disabled:
            case XInputPlusMethod.Injection:
                return XInputPlus.UnregisterApplication(profile);
        }
    }

    private static void ControllerManager_ControllerPlugged(IController Controller, bool IsPowerCycling)
    {
        // we're only interest in virtual, XInput controllers
        if (Controller is not XInputController || !Controller.IsVirtual())
            return;

        foreach (var profile in profiles.Values)
            UpdateProfileWrapper(profile);
    }

    public static Profile? GetProfileWithDefaultLayout() => profiles.Values.FirstOrDefault(p => p.Layout.IsDefaultLayout);

    #region events

    public static event DeletedEventHandler Deleted;

    public delegate void DeletedEventHandler(Profile profile);

    public static event UpdatedEventHandler Updated;

    public delegate void UpdatedEventHandler(Profile profile, UpdateSource source, bool isCurrent);

    public static event AppliedEventHandler Applied;

    public delegate void AppliedEventHandler(Profile profile, UpdateSource source);

    public static event DiscardedEventHandler Discarded;

    public delegate void DiscardedEventHandler(Profile profile);

    public static event InitializedEventHandler Initialized;

    public delegate void InitializedEventHandler();

    #endregion
}