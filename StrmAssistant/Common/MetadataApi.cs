using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Globalization;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using StrmAssistant.Mod;
using StrmAssistant.Provider;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using static StrmAssistant.Common.CommonUtility;
using static StrmAssistant.Common.LanguageUtility;

namespace StrmAssistant.Common
{
    public class MetadataApi
    {
        private readonly ILogger _logger;
        private readonly ILibraryManager _libraryManager;
        private readonly IServerConfigurationManager _configurationManager;
        private readonly ILocalizationManager _localizationManager;
        private readonly IFileSystem _fileSystem;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly IHttpClient _httpClient;

        public static MetadataRefreshOptions PersonRefreshOptions;
        
        private const int RequestIntervalMs = 100;
        private static long _lastRequestTicks;
        private static readonly TimeSpan CacheTime = TimeSpan.FromHours(6.0);
        private static readonly LruCache LruCache = new LruCache(20);

        public MetadataApi(ILibraryManager libraryManager, IFileSystem fileSystem,
            IServerConfigurationManager configurationManager, ILocalizationManager localizationManager,
            IJsonSerializer jsonSerializer, IHttpClient httpClient)
        {
            _logger = Plugin.Instance.Logger;
            _libraryManager = libraryManager;
            _configurationManager = configurationManager;
            _localizationManager = localizationManager;
            _fileSystem = fileSystem;
            _jsonSerializer = jsonSerializer;
            _httpClient = httpClient;

            PersonRefreshOptions = new MetadataRefreshOptions(fileSystem)
            {
                EnableRemoteContentProbe = false,
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllMetadata = true,
                ImageRefreshMode = MetadataRefreshMode.FullRefresh,
                ReplaceAllImages = true,
                IsAutomated = false,
                EnableThumbnailImageExtraction = false,
                EnableSubtitleDownloading = false,
                OverwriteLocalMetadataProviderIds = true,
                ForceSave = false
            };
        }

        public string GetPreferredMetadataLanguage(BaseItem item)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            var language = item.PreferredMetadataLanguage;
            if (string.IsNullOrEmpty(language))
            {
                language = item.GetParents().Select(i => i.PreferredMetadataLanguage).FirstOrDefault(i => !string.IsNullOrEmpty(i));
            }
            if (string.IsNullOrEmpty(language))
            {
                language = libraryOptions.PreferredMetadataLanguage;
            }
            if (string.IsNullOrEmpty(language))
            {
                language = _configurationManager.Configuration.PreferredMetadataLanguage;
            }

            return language;
        }

        public string GetServerPreferredMetadataLanguage()
        {
            return _configurationManager.Configuration.PreferredMetadataLanguage;
        }

        public async Task<MetadataResult<Person>> GetPersonMetadataFromMovieDb(Person item,
            string preferredMetadataLanguage, CancellationToken cancellationToken)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);

            IHasLookupInfo<PersonLookupInfo> lookupItem = item;
            var lookupInfo = lookupItem.GetLookupInfo(libraryOptions);
            lookupInfo.MetadataLanguage = preferredMetadataLanguage;

            if (GetMovieDbPersonProvider() is IRemoteMetadataProvider<Person, PersonLookupInfo> provider)
            {
                return await GetMetadataFromProvider(provider, lookupInfo, cancellationToken)
                    .ConfigureAwait(false);
            }

            return await Task.FromResult(new MetadataResult<Person>()).ConfigureAwait(false);
        }

        private IMetadataProvider GetMovieDbPersonProvider()
        {
            var metadataProviders = Plugin.Instance.ApplicationHost.GetExports<IMetadataProvider>().ToArray();
            var movieDbPersonProvider = metadataProviders
                .FirstOrDefault(provider => provider.GetType().Name == "MovieDbPersonProvider");

            return movieDbPersonProvider;
        }
        
        private Task<MetadataResult<TItemType>> GetMetadataFromProvider<TItemType, TIdType>(
            IRemoteMetadataProvider<TItemType, TIdType> provider,
            TIdType id, CancellationToken cancellationToken)
            where TItemType : BaseItem, IHasLookupInfo<TIdType>, new()
            where TIdType : ItemLookupInfo, new()
        {
            if (!(provider is IRemoteMetadataProviderWithOptions<TItemType, TIdType> providerWithOptions))
                return provider.GetMetadata(id, cancellationToken);
            var options = new RemoteMetadataFetchOptions<TIdType>
            {
                SearchInfo = id,
                DirectoryService = PersonRefreshOptions.DirectoryService
            };
            return providerWithOptions.GetMetadata(options, cancellationToken);
        }

        public string ProcessPersonInfo(string input, bool clean)
        {
            if (IsChinese(input)) input = ConvertTraditionalToSimplified(input);

            if (clean) input = CleanPersonName(input);

            return input;
        }

        public string GetCollectionOriginalLanguage(BoxSet collection)
        {
            var children = _libraryManager.GetItemList(new InternalItemsQuery
            {
                CollectionIds = new[] { collection.InternalId }
            });

            var concatenatedTitles = string.Join("|", children.Select(c => c.OriginalTitle));

            return GetLanguageByTitle(concatenatedTitles);
        }

        public string ConvertToServerLanguage(string language)
        {
            if (string.Equals(language, "pt", StringComparison.OrdinalIgnoreCase))
                return "pt-br";
            if (string.Equals(language, "por", StringComparison.OrdinalIgnoreCase))
                return "pt";
            if (string.Equals(language, "zhtw", StringComparison.OrdinalIgnoreCase))
                return "zh-tw";
            if (string.Equals(language, "zho", StringComparison.OrdinalIgnoreCase))
                return "zh-hk";
            var languageInfo =
                _localizationManager.FindLanguageInfo(language.AsSpan());
            return languageInfo != null ? languageInfo.TwoLetterISOLanguageName : language;
        }

        public void UpdateSeriesPeople(Series series)
        {
            if (!series.ProviderIds.ContainsKey("Tmdb")) return;

            var seriesPeople = _libraryManager.GetItemPeople(series);

            var seasonQuery = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { nameof(Season) },
                ParentWithPresentationUniqueKeyFromItemId = series.InternalId,
                MinIndexNumber = 1,
                OrderBy = new (string, SortOrder)[] { (ItemSortBy.IndexNumber, SortOrder.Ascending) }
            };

            var seasons = _libraryManager.GetItemList(seasonQuery);
            var peopleLists = seasons
                .Select(s => _libraryManager.GetItemPeople(s))
                .ToList();

            peopleLists.Add(seriesPeople);

            var maxPeopleCount = peopleLists.Max(seasonPeople => seasonPeople.Count);

            var combinedPeople = new List<PersonInfo>();
            var uniqueNames = new HashSet<string>();

            for (var i = 0; i < maxPeopleCount; i++)
            {
                foreach (var seasonPeople in peopleLists)
                {
                    var person = i < seasonPeople.Count ? seasonPeople[i] : null;
                    if (person != null && uniqueNames.Add(person.Name))
                    {
                        combinedPeople.Add(person);
                    }
                }
            }

            _libraryManager.UpdatePeople(series, combinedPeople);
        }

        public ISeriesMetadataProvider GetMovieDbSeriesProvider()
        {
            return new MovieDbSeriesProvider();
        }

        public async Task<T> GetMovieDbResponse<T>(string url, string cacheKey, string cachePath,
            CancellationToken cancellationToken) where T : class
        {
            var result = TryGetFromCache<T>(cacheKey, cachePath);

            if (result != null) return result;

            var num = Math.Min((RequestIntervalMs * 10000 - (DateTimeOffset.UtcNow.Ticks - _lastRequestTicks)) / 10000L,
                RequestIntervalMs);

            if (num > 0L)
            {
                _logger.Debug("Throttling Tmdb by {0} ms", num);
                await Task.Delay(Convert.ToInt32(num)).ConfigureAwait(false);
            }

            _lastRequestTicks = DateTimeOffset.UtcNow.Ticks;

            var options = new HttpRequestOptions
            {
                Url = url,
                CancellationToken = cancellationToken,
                AcceptHeader = "application/json",
                BufferContent = true,
                UserAgent = Plugin.Instance.UserAgent
            };

            try
            {
                using var response = await _httpClient.SendAsync(options, "GET").ConfigureAwait(false);
                if (response.StatusCode != HttpStatusCode.OK)
                {
                    _logger.Debug("Failed to get MovieDb response - " + response.StatusCode);
                    return null;
                }

                await using var contentStream = response.Content;
                result = _jsonSerializer.DeserializeFromStream<T>(contentStream);

                if (result is null) return null;

                AddOrUpdateCache(result, cacheKey, cachePath);

                return result;
            }
            catch (Exception e)
            {
                _logger.Debug("Failed to get MovieDb response - " + e.Message);
                return null;
            }
        }

        public async Task<T> GetMovieDbResponse<T>(string url, CancellationToken cancellationToken) where T : class
        {
            return await GetMovieDbResponse<T>(url, null, null, cancellationToken);
        }

        public T TryGetFromCache<T>(string cacheKey, string cachePath) where T : class
        {
            if (string.IsNullOrEmpty(cacheKey) || string.IsNullOrEmpty(cachePath)) return null;

            if (LruCache.TryGetFromCache(cacheKey, out T result)) return result;

            var cacheFile = _fileSystem.GetFileSystemInfo(cachePath);

            if (cacheFile.Exists && DateTimeOffset.UtcNow - _fileSystem.GetLastWriteTimeUtc(cacheFile) <= CacheTime)
            {
                result = _jsonSerializer.DeserializeFromFile<T>(cachePath);
                LruCache.AddOrUpdateCache(cacheKey, result);

                return result;
            }

            return null;
        }

        public void AddOrUpdateCache<T>(T result, string cacheKey, string cachePath)
        {
            if (result is null || string.IsNullOrEmpty(cacheKey) || string.IsNullOrEmpty(cachePath)) return;

            _fileSystem.CreateDirectory(_fileSystem.GetDirectoryName(cachePath));
            _jsonSerializer.SerializeToFile(result, cachePath);
            LruCache.AddOrUpdateCache(cacheKey, result);
        }

        public Series GetSeriesByPath(string path)
        {
            var items = _libraryManager.GetItemList(new InternalItemsQuery { Path = path });

            foreach (var item in items)
            {
                if (item is Episode episode)
                {
                    return episode.Series;
                }

                if (item is Season season)
                {
                    return season.Series;
                }

                if (item is Series series)
                {
                    return series;
                }
            }

            return null;
        }
        
        public async Task<EpisodeGroupResponse> FetchOnlineEpisodeGroup(string seriesTmdbId,
            string episodeGroupId, string language, string localEpisodeGroupPath, CancellationToken cancellationToken)
        {
            var isExternalEpisodeGroup = IsValidHttpUrl(episodeGroupId);

            var url = isExternalEpisodeGroup
                ? episodeGroupId
                : BuildMovieDbApiUrl($"tv/episode_group/{episodeGroupId}", language);

            if (isExternalEpisodeGroup)
            {
                episodeGroupId = GenerateFixedCode(episodeGroupId, "external_", 24);
            }

            var cacheKey = "tmdb_episode_group_" + seriesTmdbId + "_" + episodeGroupId;

            var cachePath = Path.Combine(Plugin.Instance.ApplicationPaths.CachePath, "tmdb-tv", seriesTmdbId,
                episodeGroupId + ".json");

            var episodeGroupResponse = await Plugin.MetadataApi
                .GetMovieDbResponse<EpisodeGroupResponse>(url, cacheKey, cachePath, cancellationToken)
                .ConfigureAwait(false);

            if (episodeGroupResponse != null && !string.IsNullOrEmpty(localEpisodeGroupPath))
            {
                if (isExternalEpisodeGroup && string.IsNullOrEmpty(episodeGroupResponse.id))
                {
                    episodeGroupResponse.id = url;
                }

                try
                {
                    var compactEpisodeGroupResponse = ConvertToCompactResponse(episodeGroupResponse);
                    _jsonSerializer.SerializeToFile(compactEpisodeGroupResponse, localEpisodeGroupPath);
                }
                catch (Exception e)
                {
                    _logger.Error("LocalEpisodeGroup - Serialization Failed" + localEpisodeGroupPath);
                    _logger.Error(e.Message);
                    _logger.Debug(e.StackTrace);
                }
            }

            return episodeGroupResponse;
        }

        public async Task<EpisodeGroupResponse> FetchLocalEpisodeGroup(string localEpisodeGroupPath)
        {
            EpisodeGroupResponse result = null;

            if (!string.IsNullOrEmpty(localEpisodeGroupPath))
            {
                if (LruCache.TryGetFromCache(localEpisodeGroupPath, out result))
                {
                    return result;
                }

                var directoryService = new DirectoryService(_logger, _fileSystem);
                var file = directoryService.GetFile(localEpisodeGroupPath);

                if (file?.Exists == true)
                {
                    try
                    {
                        result = await _jsonSerializer
                            .DeserializeFromFileAsync<EpisodeGroupResponse>(localEpisodeGroupPath)
                            .ConfigureAwait(false);

                        if (result != null)
                        {
                            LruCache.AddOrUpdateCache(localEpisodeGroupPath, result);
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.Debug("Failed to get local episode group - " + e.Message);
                        return null;
                    }
                }
            }

            return result;
        }
        
        public static string BuildMovieDbApiUrl(string endpoint, string language)
        {
            var url =
                $"{AltMovieDbConfig.CurrentMovieDbApiUrl}/3/{endpoint}?api_key={AltMovieDbConfig.CurrentMovieDbApiKey}";
            if (!string.IsNullOrEmpty(language))
                url += $"&language={language}";
            return url;
        }

        private static CompactEpisodeGroupResponse ConvertToCompactResponse(EpisodeGroupResponse response)
        {
            if (response == null) return null;

            return new CompactEpisodeGroupResponse
            {
                id = response.id,
                description = response.description,
                groups = response.groups?.Select(group => new CompactEpisodeGroup
                {
                    name = group.name,
                    order = group.order,
                    episodes = group.episodes?.Select(episode => new CompactGroupEpisode
                    {
                        episode_number = episode.episode_number,
                        season_number = episode.season_number,
                        order = episode.order
                    }).ToList()
                }).ToList()
            };
        }
    }
}
