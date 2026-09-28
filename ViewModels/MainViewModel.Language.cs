using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows.Input;
using SandstormModLauncher.Core;
using static SandstormModLauncher.Core.Loc;

namespace SandstormModLauncher.ViewModels
{
    public sealed class LanguageItem
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public override string ToString() => Name;
    }

    /// <summary>Settings > Language: the translation in use, and making or trying a translation file.</summary>
    public sealed partial class MainViewModel
    {
        public ObservableCollection<LanguageItem> Languages { get; } = new ObservableCollection<LanguageItem>();
        public ICommand MakeTranslationFileCommand { get; private set; }
        public ICommand OpenLanguagesFolderCommand { get; private set; }
        public ICommand ReloadLanguagesCommand { get; private set; }
        private LanguageItem selectedLanguage;

        private void InitLanguageCommands()
        {
            MakeTranslationFileCommand = new RelayCommand(MakeTranslationFile);
            OpenLanguagesFolderCommand = new RelayCommand(() => { Directory.CreateDirectory(Loc.UserFolder); Open(Loc.UserFolder); });
            ReloadLanguagesCommand = new RelayCommand(() =>
            {
                BuildLanguages();
                // The file may have changed: read it again.
                Loc.Use(State.Settings.Language);
                AfterLanguageChange();
                ShowToast(Loc.T("Languages read again"));
            });
        }

        private void BuildLanguages()
        {
            Languages.Clear();
            Languages.Add(new LanguageItem { Id = "", Name = "English" });
            foreach (var l in Loc.Available())
                Languages.Add(new LanguageItem { Id = l.Id, Name = l.Name + (l.BuiltIn ? "" : "  ·  " + Loc.T("from the languages folder")) });
            selectedLanguage = Languages.FirstOrDefault(l => l.Id.Equals(State.Settings.Language ?? "", StringComparison.OrdinalIgnoreCase)) ?? Languages[0];
            RaiseMany(nameof(SelectedLanguage), nameof(LanguageNote));
        }

        public LanguageItem SelectedLanguage
        {
            get => selectedLanguage;
            set
            {
                if (value == null || value == selectedLanguage) return;
                selectedLanguage = value;
                State.Settings.Language = value.Id;
                SaveSettingsSoon();
                Loc.Use(value.Id);
                AfterLanguageChange();
            }
        }

        public string LanguageNote
        {
            get
            {
                var lang = string.IsNullOrEmpty(State.Settings.Language) ? null : Loc.Available().FirstOrDefault(l => l.Id.Equals(State.Settings.Language, StringComparison.OrdinalIgnoreCase));
                if (lang == null) return "";
                int total = Loc.Template().Count(r => !r.English.StartsWith("@", StringComparison.Ordinal));
                string by = string.IsNullOrWhiteSpace(lang.Translator) ? "" : Loc.F("Translation by {0}. ", lang.Translator);
                return by + Loc.F("{0} of {1} texts are translated; the rest stay in English.", Math.Min(lang.Count, total), total);
            }
        }

        /// <summary>Everything the view model shows again, in the new language.</summary>
        private void AfterLanguageChange()
        {
            Raise(string.Empty);
            if (!Loading)
            {
                RefreshCatalogUi();
                BuildMutatorPresets();
                RebuildLiveGroups();
                foreach (var r in LiveRules) r.Relabel();
                if (State.Commands != null) BuildCommandList();
                BuildMods();
                LoadMapCycle();
                UpdateServerPlan();
                // Status lines worked out earlier, in the language of then.
                Toast = null;
                RefreshConsoleKeyStatus();
                RefreshKeyBackups();
                _ = RefreshRconStatus();
                if (!updateReady) ShowLastUpdateCheck();
                OnGameStateChanged();
                OnServerStateChanged();
            }
            RaiseMany(nameof(LanguageNote));
        }

        private void MakeTranslationFile()
        {
            string lang = State.Settings.Language;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Title = Loc.T("Save the translation file"),
                Filter = Loc.T("Translation table") + " (*.csv)|*.csv",
                InitialDirectory = Directory.CreateDirectory(Loc.UserFolder).FullName,
                FileName = (string.IsNullOrEmpty(lang) ? "my-language" : lang) + ".csv",
            };
            if (dlg.ShowDialog(System.Windows.Application.Current?.MainWindow) != true) return;
            try
            {
                Loc.MakeTranslationFile(lang, dlg.FileName);
                ShowToast(Loc.T("Translation file saved. Fill in the Translation column, save it as CSV UTF-8, and press Reload."));
                Open(Path.GetDirectoryName(dlg.FileName));
            }
            catch (Exception ex) { ShowToast(Loc.F("The file could not be saved: {0}", ex.Message)); }
        }
    }
}
