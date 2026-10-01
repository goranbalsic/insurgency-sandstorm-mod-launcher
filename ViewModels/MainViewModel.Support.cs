using System.Windows.Input;
using SandstormModLauncher.Services;

namespace SandstormModLauncher.ViewModels
{
    /// <summary>
    /// The Support page: a link to the maker's Ko-fi page and a few free ways to help. It only opens the browser when a
    /// button is pressed (the launcher itself connects nowhere for this), and holds no key or account of any kind.
    /// </summary>
    public sealed partial class MainViewModel
    {
        /// <summary>The maker's public Ko-fi page.</summary>
        public const string KofiUrl = "https://ko-fi.com/";

        public ICommand OpenKofiCommand { get; private set; }
        public ICommand CopyKofiCommand { get; private set; }
        public ICommand OpenGitHubCommand { get; private set; }

        public string SupportUrl => KofiUrl;

        private void InitSupportCommands()
        {
            OpenKofiCommand = new RelayCommand(() => Open(KofiUrl));
            CopyKofiCommand = new RelayCommand(() => CopyText(KofiUrl, "Link copied"));
            OpenGitHubCommand = new RelayCommand(() => Open("https://github.com/" + Updater.Repo));
        }
    }
}
