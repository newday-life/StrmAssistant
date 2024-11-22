using MediaBrowser.Common;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Notifications;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Drawing;
using MediaBrowser.Model.Events;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Plugins.UI;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Common;
using StrmAssistant.IntroSkip;
using StrmAssistant.Mod;
using StrmAssistant.Options;
using StrmAssistant.Options.Store;
using StrmAssistant.Options.View;
using StrmAssistant.Web.Helper;
using System;
using System.Collections.Generic;
using System.IO;

namespace StrmAssistant
{
    public class Plugin : BasePlugin, IHasThumbImage, IHasUIPages
    {
        private List<IPluginUIPageController> _pages;
        public readonly PluginOptionsStore MainOptionsStore;
        public readonly IntroSkipOptionsStore IntroSkipStore;
        public readonly MetadataEnhanceOptionsStore MetadataEnhanceStore;
        public readonly UIFunctionOptionsStore UIFunctionStore;

        public static Plugin Instance { get; private set; }
        public static LibraryApi LibraryApi { get; private set; }
        public static ChapterApi ChapterApi { get; private set; }
        public static NotificationApi NotificationApi { get; private set; }
        public static SubtitleApi SubtitleApi { get; private set; }
        public static PlaySessionMonitor PlaySessionMonitor { get; private set; }
        public static MetadataApi MetadataApi { get; private set; }

        private readonly Guid _id = new Guid("63c322b7-a371-41a3-b11f-04f8418b37d8");

        public readonly ILogger Logger;
        public readonly IApplicationHost ApplicationHost;
        public readonly IApplicationPaths ApplicationPaths;

        private readonly ILibraryManager _libraryManager;
        private readonly IUserManager _userManager;
        private readonly IUserDataManager _userDataManager;

        public Plugin(IApplicationHost applicationHost,
            IApplicationPaths applicationPaths,
            ILogManager logManager,
            IFileSystem fileSystem,
            ILibraryManager libraryManager,
            ISessionManager sessionManager,
            IItemRepository itemRepository,
            INotificationManager notificationManager,
            IMediaSourceManager mediaSourceManager,
            IMediaMountManager mediaMountManager,
            IMediaProbeManager mediaProbeManager,
            ILocalizationManager localizationManager,
            IUserManager userManager,
            IUserDataManager userDataManager,
            IFfmpegManager ffmpegManager,
            IMediaEncoder mediaEncoder,
            IJsonSerializer jsonSerializer,
            IServerApplicationHost serverApplicationHost,
            IServerConfigurationManager configurationManager)
        {
            Instance = this;
            Logger = logManager.GetLogger(Name);
            Logger.Info("Plugin is getting loaded.");
            ApplicationHost = applicationHost;
            ApplicationPaths = applicationPaths;

            _libraryManager = libraryManager;
            _userManager = userManager;
            _userDataManager = userDataManager;

            MainOptionsStore = new PluginOptionsStore(applicationHost, Logger, Name);
            IntroSkipStore = new IntroSkipOptionsStore(applicationHost, Logger, Name + "_" + nameof(IntroSkipOptions));
            MetadataEnhanceStore = new MetadataEnhanceOptionsStore(applicationHost, Logger, Name + "_" + nameof(MetadataEnhanceOptions));
            UIFunctionStore = new UIFunctionOptionsStore(applicationHost, Logger, Name + "_" + nameof(UIFunctionOptions));

            LibraryApi = new LibraryApi(libraryManager, fileSystem, mediaSourceManager, mediaMountManager, userManager);
            ChapterApi = new ChapterApi(libraryManager, itemRepository, fileSystem, applicationPaths, ffmpegManager,
                mediaEncoder, mediaMountManager, jsonSerializer, serverApplicationHost);
            PlaySessionMonitor = new PlaySessionMonitor(libraryManager, userManager, sessionManager);
            NotificationApi = new NotificationApi(notificationManager, userManager, sessionManager);
            SubtitleApi = new SubtitleApi(libraryManager, fileSystem, mediaProbeManager, localizationManager,
                itemRepository);
            MetadataApi = new MetadataApi(libraryManager, fileSystem, configurationManager, localizationManager);
            ShortcutMenuHelper.Initialize(configurationManager);

            PatchManager.Initialize();
            if (MainOptionsStore.GetOptions().GeneralOptions.CatchupMode) InitializeCatchupMode();
            if (IntroSkipStore.GetOptions().EnableIntroSkip) PlaySessionMonitor.Initialize();
            QueueManager.Initialize();

            _libraryManager.ItemAdded += OnItemAdded;
            _libraryManager.ItemUpdated += OnItemUpdated;
            _userManager.UserCreated += OnUserCreated;
            _userManager.UserDeleted += OnUserDeleted;
            _userManager.UserConfigurationUpdated += OnUserConfigurationUpdated;
        }

        public void InitializeCatchupMode()
        {
            DisposeCatchupMode();
            _userDataManager.UserDataSaved += OnUserDataSaved;
        }

        public void DisposeCatchupMode()
        {
            _userDataManager.UserDataSaved -= OnUserDataSaved;
        }

        private void OnUserCreated(object sender, GenericEventArgs<User> e)
        {
            LibraryApi.FetchUsers();
        }

        private void OnUserDeleted(object sender, GenericEventArgs<User> e)
        {
            LibraryApi.FetchUsers();
        }

        private void OnUserConfigurationUpdated(object sender, GenericEventArgs<User> e)
        {
            if (e.Argument.Policy.IsAdministrator) LibraryApi.FetchAdminOrderedViews();
        }

        private void OnItemAdded(object sender, ItemChangeEventArgs e)
        {
            if (MainOptionsStore.GetOptions().GeneralOptions.CatchupMode &&
                (MainOptionsStore.GetOptions().MediaInfoExtractOptions.ExclusiveExtract || e.Item.IsShortcut))
            {
                QueueManager.MediaInfoExtractItemQueue.Enqueue(e.Item);
            }

            if (IntroSkipStore.GetOptions().EnableIntroSkip && PlaySessionMonitor.IsLibraryInScope(e.Item))
            {
                if (!LibraryApi.HasMediaStream(e.Item))
                {
                    QueueManager.MediaInfoExtractItemQueue.Enqueue(e.Item);
                }
                else if (e.Item is Episode episode && ChapterApi.SeasonHasIntroCredits(episode))
                {
                    QueueManager.IntroSkipItemQueue.Enqueue(episode);
                }
            }

            NotificationApi.FavoritesUpdateSendNotification(e.Item);
        }

        private void OnItemUpdated(object sender, ItemChangeEventArgs e)
        {
            if (MetadataEnhanceStore.GetOptions().EnhanceMovieDbPerson &&
                (e.UpdateReason & (ItemUpdateType.MetadataDownload | ItemUpdateType.MetadataImport)) != 0)
            {
                if (e.Item is Season season && season.IndexNumber > 0)
                {
                    LibraryApi.UpdateSeriesPeople(season.Parent as Series);
                }
                else if (e.Item is Series series)
                {
                    LibraryApi.UpdateSeriesPeople(series);
                }
            }
        }

        private void OnUserDataSaved(object sender, UserDataSaveEventArgs e)
        {
            if (e.UserData.IsFavorite)
            {
                QueueManager.MediaInfoExtractItemQueue.Enqueue(e.Item);
            }
        }

        public ImageFormat ThumbImageFormat => ImageFormat.Png;

        public override string Description => "Extract MediaInfo and Enable IntroSkip";

        public override Guid Id => _id;

        public sealed override string Name => "Strm Assistant";

        public Stream GetThumbImage()
        {
            var type = GetType();
            return type.Assembly.GetManifestResourceStream(type.Namespace + ".Properties.thumb.png");
        }

        public IReadOnlyCollection<IPluginUIPageController> UIPageControllers
        {
            get
            {
                if (_pages == null)
                {
                    _pages = new List<IPluginUIPageController>
                    {
                        new MainPageController(GetPluginInfo(), _libraryManager, MainOptionsStore,
                            MetadataEnhanceStore, IntroSkipStore, UIFunctionStore)
                    };
                }

                return _pages.AsReadOnly();
            }
        }
    }
}
