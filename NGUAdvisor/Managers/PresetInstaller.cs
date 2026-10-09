using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

namespace NGUAdvisor.Managers
{
    // Route C3 Phase 1: writes the embedded goal-loadout presets (Presets/*.json) into the runtime profiles
    // dir on startup so they appear in the profile dropdown and can be toggled.
    //
    // THIS USED TO BE `if (File.Exists(dest)) continue;` AND THAT WAS THE BUG. "Never overwrite" reads as
    // the safe choice, and it is the reason audit 01 finding #11 is still live on the operator's machine a
    // month after the repo fixed it: the installed 24hr-EarlyEvil.json still lists ALLNGU twice and no
    // release could ever replace it. See PresetInstallPlan for the policy that replaces it — in short,
    // this now overwrites a preset ONLY when the copy on disk is byte-for-byte what this installer last
    // wrote, and on the one-time migration (no manifest yet) it takes a timestamped backup first and says
    // so in the log. A hand-edited preset is preserved permanently.
    public static class PresetInstaller
    {
        private const string Prefix = "NGUAdvisor.Presets.";

        public static void Install(string profilesDir)
        {
            try
            {
                if (string.IsNullOrEmpty(profilesDir) || !Directory.Exists(profilesDir)) return;

                var manifestPath = Path.Combine(profilesDir, PresetInstallPlan.ManifestFileName);
                Dictionary<string, string> records;
                try
                {
                    records = File.Exists(manifestPath)
                        ? PresetInstallPlan.ParseManifest(File.ReadAllText(manifestPath))
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
                catch
                {
                    // An unreadable manifest degrades to "no records", which costs one backup per changed
                    // file and never costs a user edit. It must not stop the whole install.
                    records = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }

                var manifestDirty = false;
                var asm = Assembly.GetExecutingAssembly();
                foreach (var res in asm.GetManifestResourceNames())
                {
                    if (!res.StartsWith(Prefix, StringComparison.Ordinal) ||
                        !res.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var fileName = res.Substring(Prefix.Length);        // e.g. "Goal-AdvDC.json"
                    var dest = Path.Combine(profilesDir, fileName);

                    string shipped;
                    using (var stream = asm.GetManifestResourceStream(res))
                    {
                        if (stream == null) continue;
                        using (var reader = new StreamReader(stream))
                            shipped = reader.ReadToEnd();
                    }

                    // The hash RECORDED and the hash COMPARED are both the normalised one. See
                    // PresetInstallPlan.Decide: comparing raw text made a no-op save in the companion's
                    // editor freeze the preset forever, because that save drops documentation keys,
                    // reflows arrays and adds absent payload keys. 0 of 30 presets survived it.
                    var shippedHashNorm = PresetInstallPlan.HashNormalized(shipped);
                    var exists = File.Exists(dest);
                    var destText = exists ? File.ReadAllText(dest) : null;
                    var destHashRaw = exists ? PresetInstallPlan.Hash(destText) : null;
                    var destHashNorm = exists ? PresetInstallPlan.HashNormalized(destText) : null;
                    string recorded;
                    if (!records.TryGetValue(fileName, out recorded)) recorded = null;

                    var action = PresetInstallPlan.Decide(exists, destHashRaw, destHashNorm,
                                                          shippedHashNorm, recorded);

                    if (action == PresetInstallPlan.Action.PreserveUserEdit)
                    {
                        Main.Log($"Preset {fileName} differs from the shipped version and you edited it — " +
                                 "keeping yours. Delete it to take the shipped version on the next launch.");
                        continue;
                    }

                    if (action == PresetInstallPlan.Action.AlreadyCurrent)
                    {
                        // Nothing to write, but adopt the hash so a LATER edit is recognisable as one.
                        // This is also the branch a no-op editor save now lands on: the file on disk may be
                        // the reformatted one, and it is deliberately LEFT ALONE - only the record moves.
                        if (!string.Equals(recorded, shippedHashNorm, StringComparison.OrdinalIgnoreCase))
                        {
                            records[fileName] = shippedHashNorm;
                            manifestDirty = true;
                        }
                        continue;
                    }

                    // NEVER DESTROY WITHOUT A COPY, and the second clause is new. BackupThenInstall is still
                    // the one-time migration. UpdateInPlace used to mean "byte-for-byte what we wrote, so
                    // there is nothing of the user's to lose" - that is no longer true, because the
                    // comparison now folds away documentation keys and formatting. When the bytes on disk
                    // are not the bytes we shipped, something of theirs is in there (comments, at least),
                    // so it gets the same timestamped backup before being replaced.
                    string backupPath = null;
                    var needsBackup = action == PresetInstallPlan.Action.BackupThenInstall ||
                                      (action == PresetInstallPlan.Action.UpdateInPlace &&
                                       !string.Equals(destText, shipped, StringComparison.Ordinal));
                    if (needsBackup)
                    {
                        var backupDir = Path.Combine(profilesDir, PresetInstallPlan.BackupFolderName);
                        if (!Directory.Exists(backupDir)) Directory.CreateDirectory(backupDir);
                        backupPath = Path.Combine(backupDir, PresetInstallPlan.BackupName(fileName, DateTime.Now));
                        File.Copy(dest, backupPath, true);
                    }

                    File.WriteAllText(dest, shipped);
                    records[fileName] = shippedHashNorm;
                    manifestDirty = true;

                    if (action == PresetInstallPlan.Action.Install)
                        Main.Log($"Installed goal preset: {fileName}");
                    else if (action == PresetInstallPlan.Action.UpdateInPlace && backupPath == null)
                        Main.Log($"Updated preset {fileName} to the shipped version (your copy was unmodified).");
                    else if (action == PresetInstallPlan.Action.UpdateInPlace)
                        Main.Log($"Updated preset {fileName} to the shipped version. Your copy differed only in " +
                                 $"formatting or comments and was saved to {HostPath.For(backupPath)}");
                    else
                        Main.Log($"Replaced preset {fileName} with the shipped version. Your previous copy was " +
                                 $"saved to {HostPath.For(backupPath)}");
                }

                if (manifestDirty)
                {
                    try { File.WriteAllText(manifestPath, PresetInstallPlan.SerializeManifest(records)); }
                    catch (Exception e) { Main.LogDebug($"PresetInstaller could not write the manifest: {e.Message}"); }
                }
            }
            catch (Exception e)
            {
                Main.LogDebug($"PresetInstaller failed: {e.Message}");
            }
        }
    }
}
