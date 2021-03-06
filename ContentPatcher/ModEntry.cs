using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using ContentPatcher.Framework;
using ContentPatcher.Framework.Commands;
using ContentPatcher.Framework.Conditions;
using ContentPatcher.Framework.ConfigModels;
using ContentPatcher.Framework.Patches;
using ContentPatcher.Framework.Tokens;
using Pathoschild.Stardew.Common.Utilities;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace ContentPatcher
{
    /// <summary>The mod entry point.</summary>
    internal class ModEntry : Mod
    {
        /*********
        ** Properties
        *********/
        /// <summary>The name of the file which contains patch metadata.</summary>
        private readonly string PatchFileName = "content.json";

        /// <summary>The name of the file which contains player settings.</summary>
        private readonly string ConfigFileName = "config.json";

        /// <summary>The supported format versions.</summary>
        private readonly string[] SupportedFormatVersions = { "1.0", "1.3", "1.4", "1.5" };

        /// <summary>The minimum format versions for newer condition types.</summary>
        private readonly IDictionary<ConditionType, string> MinimumTokenVersions = new Dictionary<ConditionType, string>
        {
            [ConditionType.DayEvent] = "1.4",
            [ConditionType.HasFlag] = "1.4",
            [ConditionType.HasSeenEvent] = "1.4",
            [ConditionType.Hearts] = "1.4",
            [ConditionType.Relationship] = "1.4",
            [ConditionType.Spouse] = "1.4",
            [ConditionType.Year] = "1.5"
        };

        /// <summary>Manages the available contextual tokens.</summary>
        private TokenManager TokenManager;

        /// <summary>Manages loaded patches.</summary>
        private PatchManager PatchManager;

        /// <summary>Handles the 'patch' console command.</summary>
        private CommandHandler CommandHandler;

        /// <summary>The mod configuration.</summary>
        private ModConfig Config;

        /// <summary>The debug overlay (if enabled).</summary>
        private DebugOverlay DebugOverlay;


        /*********
        ** Public methods
        *********/
        /// <summary>The mod entry point, called after the mod is first loaded.</summary>
        /// <param name="helper">Provides simplified APIs for writing mods.</param>
        public override void Entry(IModHelper helper)
        {
            this.Config = helper.ReadConfig<ModConfig>();

            // fetch content packs
            ManagedContentPack[] contentPacks = this.GetContentPacks().ToArray();
            string[] installedMods =
                (contentPacks.Select(p => p.Manifest.UniqueID))
                .Concat(helper.ModRegistry.GetAll().Select(p => p.Manifest.UniqueID))
                .OrderByIgnoreCase(p => p)
                .ToArray();

            // load content packs and context
            this.TokenManager = new TokenManager(helper.Content, installedMods);
            this.PatchManager = new PatchManager(this.Monitor, this.TokenManager, this.Config.VerboseLog);
            this.LoadContentPacks(contentPacks);
            this.TokenManager.UpdateContext();

            // register patcher
            helper.Content.AssetLoaders.Add(this.PatchManager);
            helper.Content.AssetEditors.Add(this.PatchManager);

            // set up events
            if (this.Config.EnableDebugFeatures)
                InputEvents.ButtonPressed += this.InputEvents_ButtonPressed;
            SaveEvents.AfterReturnToTitle += this.SaveEvents_AfterReturnToTitle;
            TimeEvents.AfterDayStarted += this.TimeEvents_AfterDayStarted;

            // set up commands
            this.CommandHandler = new CommandHandler(this.TokenManager, this.PatchManager, this.Monitor, this.UpdateContext);
            helper.ConsoleCommands.Add(this.CommandHandler.CommandName, $"Starts a Content Patcher command. Type '{this.CommandHandler.CommandName} help' for details.", (name, args) => this.CommandHandler.Handle(args));
        }


        /*********
        ** Private methods
        *********/
        /****
        ** Event handlers
        ****/
        /// <summary>The method invoked when the player presses a button.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        private void InputEvents_ButtonPressed(object sender, EventArgsInput e)
        {
            if (this.Config.EnableDebugFeatures)
            {
                // toggle overlay
                if (this.Config.Controls.ToggleDebug.Contains(e.Button))
                {
                    if (this.DebugOverlay == null)
                        this.DebugOverlay = new DebugOverlay(this.Helper.Content);
                    else
                    {
                        this.DebugOverlay.Dispose();
                        this.DebugOverlay = null;
                    }
                    return;
                }

                // cycle textures
                if (this.DebugOverlay != null)
                {
                    if (this.Config.Controls.DebugPrevTexture.Contains(e.Button))
                        this.DebugOverlay.PrevTexture();
                    if (this.Config.Controls.DebugNextTexture.Contains(e.Button))
                        this.DebugOverlay.NextTexture();
                }
            }
        }

        /// <summary>The method invoked when the player returns to the title screen.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        private void SaveEvents_AfterReturnToTitle(object sender, EventArgs e)
        {
            this.UpdateContext();
        }

        /// <summary>The method invoked when a new day starts.</summary>
        /// <param name="sender">The event sender.</param>
        /// <param name="e">The event data.</param>
        private void TimeEvents_AfterDayStarted(object sender, EventArgs e)
        {
            this.UpdateContext();
        }

        /****
        ** Methods
        ****/
        /// <summary>Update the current context.</summary>
        private void UpdateContext()
        {
            this.TokenManager.UpdateContext();
            this.PatchManager.UpdateContext(this.Helper.Content);
        }

        /// <summary>Load the registered content packs.</summary>
        /// <returns>Returns the loaded content pack IDs.</returns>
        [SuppressMessage("ReSharper", "AccessToModifiedClosure", Justification = "The value is used immediately, so this isn't an issue.")]
        private IEnumerable<ManagedContentPack> GetContentPacks()
        {
            this.VerboseLog("Preloading content packs...");

            foreach (IContentPack contentPack in this.Helper.GetContentPacks())
            {
                try
                {
                    // validate content.json has required fields
                    ContentConfig content = contentPack.ReadJsonFile<ContentConfig>(this.PatchFileName);
                    if (content == null)
                    {
                        this.Monitor.Log($"Ignored content pack '{contentPack.Manifest.Name}' because it has no {this.PatchFileName} file.", LogLevel.Error);
                        continue;
                    }
                    if (content.Format == null || content.Changes == null)
                    {
                        this.Monitor.Log($"Ignored content pack '{contentPack.Manifest.Name}' because it doesn't specify the required {nameof(ContentConfig.Format)} or {nameof(ContentConfig.Changes)} fields.", LogLevel.Error);
                        continue;
                    }

                    // validate format
                    if (!this.ValidateFormatVersion(contentPack, content, usesConfig: content.ConfigSchema?.Any() == true))
                        continue;
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Error preloading content pack '{contentPack.Manifest.Name}'. Technical details:\n{ex}", LogLevel.Error);
                    continue;
                }

                yield return new ManagedContentPack(contentPack);
            }
        }

        /// <summary>Load the patches from all registered content packs.</summary>
        /// <returns>Returns the loaded content pack IDs.</returns>
        [SuppressMessage("ReSharper", "AccessToModifiedClosure", Justification = "The value is used immediately, so this isn't an issue.")]
        private void LoadContentPacks(IEnumerable<ManagedContentPack> contentPacks)
        {
            ISemanticVersion latestFormatVersion = new SemanticVersion(this.SupportedFormatVersions.Last());
            InvariantDictionary<ISemanticVersion> minimumTokenVersions = new InvariantDictionary<ISemanticVersion>(
                this.MinimumTokenVersions.ToDictionary(p => p.Key.ToString(), p => (ISemanticVersion)new SemanticVersion(p.Value))
            );
            ConfigFileHandler configFileHandler = new ConfigFileHandler(this.ConfigFileName, this.ParseCommaDelimitedField, (pack, label, reason) => this.Monitor.Log($"Ignored {pack.Manifest.Name} > {label}: {reason}"));
            foreach (ManagedContentPack pack in contentPacks)
            {
                this.VerboseLog($"Loading content pack '{pack.Manifest.Name}'...");

                try
                {
                    ContentConfig content = pack.ReadJsonFile<ContentConfig>(this.PatchFileName);
                    ModTokenContext tokenContext = this.TokenManager.TrackLocalTokens(pack.Pack);
                    {
                        // load config.json
                        InvariantDictionary<ConfigField> config = configFileHandler.Read(pack, content.ConfigSchema);
                        configFileHandler.Save(pack, config, this.Helper);
                        if (config.Any())
                            this.VerboseLog($"   found config.json with {config.Count} fields...");

                        // load config tokens
                        foreach (KeyValuePair<string, ConfigField> pair in config)
                        {
                            ConfigField field = pair.Value;
                            tokenContext.Add(new ImmutableToken(pair.Key, field.Value, allowedValues: field.AllowValues, canHaveMultipleValues: field.AllowMultiple));
                        }

                        // load dynamic tokens
                        foreach (DynamicTokenConfig entry in content.DynamicTokens ?? new DynamicTokenConfig[0])
                        {
                            void LogSkip(string reason) => this.Monitor.Log($"Ignored {pack.Manifest.Name} > dynamic token '{entry.Name}': {reason}", LogLevel.Warn);

                            // validate token key
                            if (!TokenName.TryParse(entry.Name, out TokenName name))
                            {
                                LogSkip("the name could not be parsed as a token key.");
                                continue;
                            }
                            if (name.HasSubkey())
                            {
                                LogSkip("the token name cannot contain a subkey (:).");
                                continue;
                            }
                            if (name.TryGetConditionType(out ConditionType conflictingType))
                            {
                                LogSkip($"conflicts with global token '{conflictingType}'.");
                                continue;
                            }
                            if (config.ContainsKey(name.Key))
                            {
                                LogSkip($"conflicts with player config token '{conflictingType}'.");
                                continue;
                            }

                            // parse values
                            InvariantHashSet values = entry.Value != null ? this.ParseCommaDelimitedField(entry.Value) : new InvariantHashSet();

                            // parse conditions
                            ConditionDictionary conditions;
                            {
                                if (!this.TryParseConditions(entry.When, tokenContext, content.Format, latestFormatVersion, minimumTokenVersions, out conditions, out string error))
                                {
                                    this.Monitor.Log($"Ignored {pack.Manifest.Name} > '{entry.Name}' token: its {nameof(DynamicTokenConfig.When)} field is invalid: {error}.", LogLevel.Warn);
                                    continue;
                                }
                            }

                            // add token
                            tokenContext.Add(new DynamicTokenValue(name, values, conditions));
                        }
                    }

                    // load patches
                    content.Changes = this.SplitPatches(content.Changes).ToArray();
                    this.NamePatches(pack, content.Changes);
                    foreach (PatchConfig patch in content.Changes)
                    {
                        this.VerboseLog($"   loading {patch.LogName}...");
                        this.LoadPatch(pack, content, patch, tokenContext, latestFormatVersion, minimumTokenVersions, logSkip: reasonPhrase => this.Monitor.Log($"Ignored {patch.LogName}: {reasonPhrase}", LogLevel.Warn));
                    }
                }
                catch (Exception ex)
                {
                    this.Monitor.Log($"Error loading content pack '{pack.Manifest.Name}'. Technical details:\n{ex}", LogLevel.Error);
                    continue;
                }
            }
        }

        /// <summary>Validate that a content pack doesn't use features that aren't available for its version.</summary>
        /// <param name="pack">The content pack.</param>
        /// <param name="content">The mod's content data.</param>
        /// <param name="usesConfig">Whether the mod uses config settings.</param>
        [SuppressMessage("ReSharper", "InconsistentNaming", Justification = "Variables are named deliberately to avoid ambiguity.")]
        private bool ValidateFormatVersion(IContentPack pack, ContentConfig content, bool usesConfig)
        {
            // init
            bool predates13 = content.Format.IsOlderThan("1.3");
            bool predates15 = content.Format.IsOlderThan("1.5");
            bool LogFailed(string reason)
            {
                this.Monitor.Log($"Loading content pack '{pack.Manifest.Name}' failed. It specifies format version {content.Format}, but {reason}.", LogLevel.Error);
                return false;
            }

            // invalid format version
            if (!this.SupportedFormatVersions.Contains(content.Format.ToString()))
                return LogFailed($"unsupported format {content.Format} (supported version: {string.Join(", ", this.SupportedFormatVersions)}).");

            // 1.3 adds config.json
            if (predates13 && usesConfig)
                return LogFailed($"uses the {nameof(ContentConfig.ConfigSchema)} field added in 1.3");

            // 1.5 adds dynamic tokens
            if (predates15 && content.DynamicTokens?.Any() == true)
                return LogFailed($"uses the {nameof(ContentConfig.DynamicTokens)} field added in 1.5");

            // check patch format
            foreach (PatchConfig patch in content.Changes)
            {
                // 1.3 adds tokens in FromFile
                if (predates13 && patch.FromFile != null && patch.FromFile.Contains("{{"))
                    return LogFailed("uses the {{token}} feature added in 1.3");

                // 1.3 adds When
                if (predates13 && content.Changes.Any(p => p.When != null && p.When.Any()))
                    return LogFailed($"uses the condition feature ({nameof(ContentConfig.Changes)}.{nameof(PatchConfig.When)} field) added in 1.3");

                // 1.5 adds multiple Target values
                if (predates15 && patch.Target.Contains(","))
                    return LogFailed($"specifies multiple {nameof(PatchConfig.Target)} values which requires 1.5 or later");
            }

            return true;
        }

        /// <summary>Split patches with multiple target values.</summary>
        /// <param name="patches">The patches to split.</param>
        private IEnumerable<PatchConfig> SplitPatches(IEnumerable<PatchConfig> patches)
        {
            foreach (PatchConfig patch in patches)
            {
                if (string.IsNullOrWhiteSpace(patch.Target) || !patch.Target.Contains(","))
                {
                    yield return patch;
                    continue;
                }

                int i = 0;
                foreach (string target in patch.Target.Split(','))
                {
                    i++;
                    yield return new PatchConfig(patch)
                    {
                        LogName = !string.IsNullOrWhiteSpace(patch.LogName) ? $"{patch.LogName} {"".PadRight(i, 'I')}" : "",
                        Target = target.Trim()
                    };
                }
            }
        }

        /// <summary>Set a unique name for all patches in a content pack.</summary>
        /// <param name="contentPack">The content pack.</param>
        /// <param name="patches">The patches to name.</param>
        private void NamePatches(ManagedContentPack contentPack, PatchConfig[] patches)
        {
            // add default log names
            foreach (PatchConfig patch in patches)
            {
                if (string.IsNullOrWhiteSpace(patch.LogName))
                    patch.LogName = $"{patch.Action} {patch.Target}";
            }

            // detect duplicate names
            InvariantHashSet duplicateNames = new InvariantHashSet(
                from patch in patches
                group patch by patch.LogName into nameGroup
                where nameGroup.Count() > 1
                select nameGroup.Key
            );

            // make names unique
            int i = 0;
            foreach (PatchConfig patch in patches)
            {
                i++;

                if (duplicateNames.Contains(patch.LogName))
                    patch.LogName = $"entry #{i} ({patch.LogName})";

                patch.LogName = $"{contentPack.Manifest.Name} > {patch.LogName}";
            }
        }

        /// <summary>Load one patch from a content pack's <c>content.json</c> file.</summary>
        /// <param name="pack">The content pack being loaded.</param>
        /// <param name="contentConfig">The content pack's config.</param>
        /// <param name="entry">The change to load.</param>
        /// <param name="tokenContext">The tokens available for this content pack.</param>
        /// <param name="latestFormatVersion">The latest format version.</param>
        /// <param name="minumumTokenVersions">The minimum format versions for newer condition types.</param>
        /// <param name="logSkip">The callback to invoke with the error reason if loading it fails.</param>
        private bool LoadPatch(ManagedContentPack pack, ContentConfig contentConfig, PatchConfig entry, IContext tokenContext, ISemanticVersion latestFormatVersion, InvariantDictionary<ISemanticVersion> minumumTokenVersions, Action<string> logSkip)
        {
            bool TrackSkip(string reason, bool warn = true)
            {
                this.PatchManager.AddPermanentlyDisabled(new DisabledPatch(entry.LogName, entry.Action, entry.Target, pack, reason));
                if (warn)
                    logSkip(reason);
                return false;
            }

            try
            {
                // normalise patch fields
                if (entry.When == null)
                    entry.When = new InvariantDictionary<string>();

                // parse action
                if (!Enum.TryParse(entry.Action, true, out PatchType action))
                {
                    return TrackSkip(string.IsNullOrWhiteSpace(entry.Action)
                        ? $"must set the {nameof(PatchConfig.Action)} field."
                        : $"invalid {nameof(PatchConfig.Action)} value '{entry.Action}', expected one of: {string.Join(", ", Enum.GetNames(typeof(PatchType)))}."
                    );
                }

                // parse target asset
                TokenString assetName;
                {
                    if (string.IsNullOrWhiteSpace(entry.Target))
                        return TrackSkip($"must set the {nameof(PatchConfig.Target)} field.");
                    if (!this.TryParseTokenString(entry.Target, tokenContext, out string error, out assetName))
                        return TrackSkip($"the {nameof(PatchConfig.Target)} is invalid: {error}");
                }

                // parse 'enabled'
                bool enabled = true;
                {
                    if (entry.Enabled != null && !this.TryParseEnabled(entry.Enabled, tokenContext, out string error, out enabled))
                        return TrackSkip($"invalid {nameof(PatchConfig.Enabled)} value '{entry.Enabled}': {error}");
                }

                // parse conditions
                ConditionDictionary conditions;
                {
                    if (!this.TryParseConditions(entry.When, tokenContext, contentConfig.Format, latestFormatVersion, minumumTokenVersions, out conditions, out string error))
                        return TrackSkip($"the {nameof(PatchConfig.When)} field is invalid: {error}.");
                }

                // get patch instance
                IPatch patch;
                switch (action)
                {
                    // load asset
                    case PatchType.Load:
                        {
                            // init patch
                            if (!this.TryPrepareLocalAsset(pack, entry.FromFile, tokenContext, out string error, out TokenString fromAsset))
                                return TrackSkip(error);
                            patch = new LoadPatch(entry.LogName, pack, assetName, conditions, fromAsset, this.Helper.Content.NormaliseAssetName);
                        }
                        break;

                    // edit data
                    case PatchType.EditData:
                        {
                            // validate
                            if (entry.Entries == null && entry.Fields == null)
                                return TrackSkip($"either {nameof(PatchConfig.Entries)} or {nameof(PatchConfig.Fields)} must be specified for a '{action}' change.");
                            if (entry.Entries != null && entry.Entries.Any(p => p.Value != null && p.Value.Trim() == ""))
                                return TrackSkip($"the {nameof(PatchConfig.Entries)} can't contain empty values.");
                            if (entry.Fields != null && entry.Fields.Any(p => p.Value == null || p.Value.Any(n => n.Value == null)))
                                return TrackSkip($"the {nameof(PatchConfig.Fields)} can't contain empty values.");

                            // parse entries
                            List<EditDataPatchRecord> entries = new List<EditDataPatchRecord>();
                            if (entry.Entries != null)
                            {
                                foreach (KeyValuePair<string, string> pair in entry.Entries)
                                {
                                    if (!this.TryParseTokenString(pair.Key, tokenContext, out string keyError, out TokenString key))
                                        return TrackSkip($"the {nameof(PatchConfig.Entries)} > '{key}' entry key is invalid: {keyError}.");
                                    if (!this.TryParseTokenString(pair.Value, tokenContext, out string error, out TokenString value))
                                        return TrackSkip($"the {nameof(PatchConfig.Entries)} > '{key}' entry value is invalid: {error}.");
                                    entries.Add(new EditDataPatchRecord(key, value));
                                }
                            }

                            // parse fields
                            List<EditDataPatchField> fields = new List<EditDataPatchField>();
                            if (entry.Fields != null)
                            {
                                foreach (KeyValuePair<string, IDictionary<int, string>> recordPair in entry.Fields)
                                {
                                    if (!this.TryParseTokenString(recordPair.Key, tokenContext, out string keyError, out TokenString key))
                                        return TrackSkip($"the {nameof(PatchConfig.Fields)} > '{keyError}' field key is invalid: {keyError}.");
                                    foreach (var fieldPair in recordPair.Value)
                                    {
                                        int field = fieldPair.Key;
                                        if (!this.TryParseTokenString(fieldPair.Value, tokenContext, out string valueError, out TokenString value))
                                            return TrackSkip($"the {nameof(PatchConfig.Fields)} > '{key}' > {field} field is invalid: {valueError}.");

                                        fields.Add(new EditDataPatchField(key, field, value));
                                    }
                                }
                            }

                            // save
                            patch = new EditDataPatch(entry.LogName, pack, assetName, conditions, entries, fields, this.Monitor, this.Helper.Content.NormaliseAssetName);
                        }
                        break;

                    // edit image
                    case PatchType.EditImage:
                        {
                            // read patch mode
                            PatchMode patchMode = PatchMode.Replace;
                            if (!string.IsNullOrWhiteSpace(entry.PatchMode) && !Enum.TryParse(entry.PatchMode, true, out patchMode))
                                return TrackSkip($"the {nameof(PatchConfig.PatchMode)} is invalid. Expected one of these values: [{string.Join(", ", Enum.GetNames(typeof(PatchMode)))}].");

                            // save
                            if (!this.TryPrepareLocalAsset(pack, entry.FromFile, tokenContext, out string error, out TokenString fromAsset))
                                return TrackSkip(error);
                            patch = new EditImagePatch(entry.LogName, pack, assetName, conditions, fromAsset, entry.FromArea, entry.ToArea, patchMode, this.Monitor, this.Helper.Content.NormaliseAssetName);
                        }
                        break;

                    default:
                        return TrackSkip($"unsupported patch type '{action}'.");
                }

                // skip if not enabled
                // note: we process the patch even if it's disabled, so any errors are caught by the modder instead of only failing after the patch is enabled.
                if (!enabled)
                    return TrackSkip($"{nameof(PatchConfig.Enabled)} is false.", warn: false);

                // save patch
                this.PatchManager.Add(patch);
                return true;
            }
            catch (Exception ex)
            {
                return TrackSkip($"error reading info. Technical details:\n{ex}");
            }
        }

        /// <summary>Normalise and parse the given condition values.</summary>
        /// <param name="raw">The raw condition values to normalise.</param>
        /// <param name="tokenContext">The tokens available for this content pack.</param>
        /// <param name="formatVersion">The format version specified by the content pack.</param>
        /// <param name="latestFormatVersion">The latest format version.</param>
        /// <param name="minumumTokenVersions">The minimum format versions for newer condition types.</param>
        /// <param name="conditions">The normalised conditions.</param>
        /// <param name="error">An error message indicating why normalisation failed.</param>
        private bool TryParseConditions(InvariantDictionary<string> raw, IContext tokenContext, ISemanticVersion formatVersion, ISemanticVersion latestFormatVersion, InvariantDictionary<ISemanticVersion> minumumTokenVersions, out ConditionDictionary conditions, out string error)
        {
            conditions = new ConditionDictionary();

            // no conditions
            if (raw == null || !raw.Any())
            {
                error = null;
                return true;
            }

            // parse conditions
            foreach (KeyValuePair<string, string> pair in raw)
            {
                // parse condition key
                if (!TokenName.TryParse(pair.Key, out TokenName name))
                {
                    error = $"'{pair.Key}' isn't a valid token name";
                    conditions = null;
                    return false;
                }

                // get token
                IToken token = tokenContext.GetToken(name, enforceContext: false);
                if (token == null)
                {
                    error = $"'{pair.Key}' isn't a valid condition; must be one of {string.Join(", ", tokenContext.GetTokens(enforceContext: false).Select(p => p.Name).OrderBy(p => p))}";
                    conditions = null;
                    return false;
                }

                // validate subkeys
                if (!token.CanHaveSubkeys)
                {
                    if (name.HasSubkey())
                    {
                        error = $"{name.Key} conditions don't allow subkeys (:)";
                        conditions = null;
                        return false;
                    }
                }
                else if (token.RequiresSubkeys)
                {
                    if (!name.HasSubkey())
                    {
                        error = $"{name.Key} conditions must specify a token subkey (see readme for usage)";
                        conditions = null;
                        return false;
                    }
                }

                // check compatibility
                if (minumumTokenVersions.TryGetValue(name.Key, out ISemanticVersion minVersion) && minVersion.IsNewerThan(formatVersion))
                {
                    error = $"{name} isn't available with format version {formatVersion} (change the {nameof(ContentConfig.Format)} field to {latestFormatVersion} to use newer features)";
                    conditions = null;
                    return false;
                }

                // parse values
                InvariantHashSet values = this.ParseCommaDelimitedField(pair.Value);
                if (!values.Any())
                {
                    error = $"{name} can't be empty";
                    conditions = null;
                    return false;
                }

                // restrict to allowed values
                InvariantHashSet rawValidValues = token.GetAllowedValues(name);
                if (rawValidValues?.Any() == true)
                {
                    InvariantHashSet validValues = new InvariantHashSet(rawValidValues);
                    {
                        string[] invalidValues = values.ExceptIgnoreCase(validValues).ToArray();
                        if (invalidValues.Any())
                        {
                            error = $"invalid {name} values ({string.Join(", ", invalidValues)}); expected one of {string.Join(", ", validValues)}";
                            conditions = null;
                            return false;
                        }
                    }
                }

                // perform custom validation
                if (!token.TryCustomValidation(values, out string customError))
                {
                    error = $"invalid {name} values: {customError}";
                    conditions = null;
                    return false;
                }

                // create condition
                conditions[name] = new Condition(name, values);
            }

            // return parsed conditions
            error = null;
            return true;
        }

        /// <summary>Parse a comma-delimited set of case-insensitive condition values.</summary>
        /// <param name="field">The field value to parse.</param>
        public InvariantHashSet ParseCommaDelimitedField(string field)
        {
            if (string.IsNullOrWhiteSpace(field))
                return new InvariantHashSet();

            IEnumerable<string> values = (
                from value in field.Split(',')
                where !string.IsNullOrWhiteSpace(value)
                select value.Trim()
            );
            return new InvariantHashSet(values);
        }

        /// <summary>Parse a boolean <see cref="PatchConfig.Enabled"/> value from a string which can contain tokens, and validate that it's valid.</summary>
        /// <param name="rawValue">The raw string which may contain tokens.</param>
        /// <param name="tokenContext">The tokens available for this content pack.</param>
        /// <param name="error">An error phrase indicating why parsing failed (if applicable).</param>
        /// <param name="parsed">The parsed value.</param>
        private bool TryParseEnabled(string rawValue, IContext tokenContext, out string error, out bool parsed)
        {
            parsed = false;

            // analyse string
            if (!this.TryParseTokenString(rawValue, tokenContext, out error, out TokenString tokenString))
                return false;

            // validate & extract tokens
            string text = rawValue;
            if (tokenString.HasAnyTokens)
            {
                // only one token allowed
                if (!tokenString.IsSingleTokenOnly)
                {
                    error = "can't be treated as a true/false value because it contains multiple tokens.";
                    return false;
                }

                // check token options
                TokenName tokenName = tokenString.Tokens.First();
                IToken token = tokenContext.GetToken(tokenName, enforceContext: false);
                InvariantHashSet allowedValues = token?.GetAllowedValues(tokenName);
                if (token == null || token.IsMutable || !token.IsValidInContext)
                {
                    error = $"can only use static tokens in this field, consider using a {nameof(PatchConfig.When)} condition instead.";
                    return false;
                }
                if (allowedValues == null || !allowedValues.All(p => bool.TryParse(p, out _)))
                {
                    error = "that token isn't restricted to 'true' or 'false'.";
                    return false;
                }
                if (token.CanHaveMultipleValues(tokenName))
                {
                    error = "can't be treated as a true/false value because that token can have multiple values.";
                    return false;
                }

                text = token.GetValues(tokenName).First();
            }

            // parse text
            if (!bool.TryParse(text, out parsed))
            {
                error = $"can't parse {tokenString.Raw} as a true/false value.";
                return false;
            }
            return true;
        }

        /// <summary>Parse a string which can contain tokens, and validate that it's valid.</summary>
        /// <param name="rawValue">The raw string which may contain tokens.</param>
        /// <param name="tokenContext">The tokens available for this content pack.</param>
        /// <param name="error">An error phrase indicating why parsing failed (if applicable).</param>
        /// <param name="parsed">The parsed value.</param>
        private bool TryParseTokenString(string rawValue, IContext tokenContext, out string error, out TokenString parsed)
        {
            // parse
            parsed = new TokenString(rawValue, tokenContext);

            // validate unknown tokens
            if (parsed.InvalidTokens.Any())
            {
                error = $"found unknown tokens ({string.Join(", ", parsed.InvalidTokens.OrderBy(p => p))})";
                parsed = null;
                return false;
            }

            // validate tokens
            foreach (TokenName tokenName in parsed.Tokens)
            {
                IToken token = tokenContext.GetToken(tokenName, enforceContext: false);
                if (token == null)
                {
                    error = $"{{{{{tokenName}}}}} can't be used as a token because that token could not be found."; // should never happen
                    parsed = null;
                    return false;
                }
                if (token.CanHaveMultipleValues(tokenName))
                {
                    error = $"{{{{{tokenName}}}}} can't be used as a token because it can have multiple values.";
                    parsed = null;
                    return false;
                }
            }

            // looks OK
            error = null;
            return true;
        }


        /// <summary>Prepare a local asset file for a patch to use.</summary>
        /// <param name="pack">The content pack being loaded.</param>
        /// <param name="path">The asset path in the content patch.</param>
        /// <param name="tokenContext">The tokens available for this content pack.</param>
        /// <param name="error">The error reason if preparing the asset fails.</param>
        /// <param name="tokenedPath">The parsed value.</param>
        /// <returns>Returns whether the local asset was successfully prepared.</returns>
        private bool TryPrepareLocalAsset(ManagedContentPack pack, string path, IContext tokenContext, out string error, out TokenString tokenedPath)
        {
            // normalise raw value
            path = this.NormaliseLocalAssetPath(pack, path);
            if (path == null)
            {
                error = $"must set the {nameof(PatchConfig.FromFile)} field for this action type.";
                tokenedPath = null;
                return false;
            }

            // tokenise
            if (!this.TryParseTokenString(path, tokenContext, out string tokenError, out tokenedPath))
            {
                error = $"the {nameof(PatchConfig.FromFile)} is invalid: {tokenError}";
                tokenedPath = null;
                return false;
            }

            // looks OK
            error = null;
            return true;
        }

        /// <summary>Get a normalised file path relative to the content pack folder.</summary>
        /// <param name="contentPack">The content pack.</param>
        /// <param name="path">The relative asset path.</param>
        private string NormaliseLocalAssetPath(ManagedContentPack contentPack, string path)
        {
            // normalise asset name
            if (string.IsNullOrWhiteSpace(path))
                return null;
            string newPath = this.Helper.Content.NormaliseAssetName(path);

            // add .xnb extension if needed (it's stripped from asset names)
            string fullPath = contentPack.GetFullPath(newPath);
            if (!File.Exists(fullPath))
            {
                if (File.Exists($"{fullPath}.xnb") || Path.GetExtension(path) == ".xnb")
                    newPath += ".xnb";
            }

            return newPath;
        }

        /// <summary>Log a message if <see cref="ModConfig.VerboseLog"/> is enabled.</summary>
        /// <param name="message">The message to log.</param>
        /// <param name="level">The log level.</param>
        private void VerboseLog(string message, LogLevel level = LogLevel.Trace)
        {
            if (this.Config.VerboseLog)
                this.Monitor.Log(message, level);
        }
    }
}
