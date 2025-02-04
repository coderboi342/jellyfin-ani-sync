using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using jellyfin_ani_sync.Api;
using jellyfin_ani_sync.Configuration;
using jellyfin_ani_sync.Helpers;
using jellyfin_ani_sync.Models;
using Jellyfin.Data.Entities;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.IO;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Episode = MediaBrowser.Controller.Entities.TV.Episode;

namespace jellyfin_ani_sync {
    public class ServerEntry : IServerEntryPoint {
        private readonly ISessionManager _sessionManager;
        private readonly ILogger<ServerEntry> _logger;
        private readonly MalApiCalls _malApiCalls;
        private UserConfig _userConfig;
        private Type _animeType;
        private readonly ILibraryManager _libraryManager;
        private readonly IFileSystem _fileSystem;

        public ServerEntry(ISessionManager sessionManager, ILoggerFactory loggerFactory,
            IHttpClientFactory httpClientFactory, ILibraryManager libraryManager, IFileSystem fileSystem,
            IServerApplicationHost serverApplicationHost, IHttpContextAccessor httpContextAccessor) {
            _sessionManager = sessionManager;
            _logger = loggerFactory.CreateLogger<ServerEntry>();
            _malApiCalls = new MalApiCalls(httpClientFactory, loggerFactory, serverApplicationHost, httpContextAccessor);
            _libraryManager = libraryManager;
            _fileSystem = fileSystem;
        }

        public Task RunAsync() {
            _sessionManager.PlaybackStopped += PlaybackStopped;
            return Task.CompletedTask;
        }

        public async void PlaybackStopped(object sender, PlaybackStopEventArgs e) {
            var video = e.Item as Video;
            Episode episode = video as Episode;
            Movie movie = video as Movie;
            if (video is Episode) {
                _animeType = typeof(Episode);
            } else if (video is Movie) {
                _animeType = typeof(Movie);
                video.IndexNumber = 1;
            }

            if (Plugin.Instance.PluginConfiguration.ProviderApiAuth is { Length: > 0 }) {
                foreach (User user in e.Users) {
                    _userConfig = Plugin.Instance.PluginConfiguration.UserConfig.FirstOrDefault(item => item.UserId == user.Id);
                    if (_userConfig == null) {
                        _logger.LogWarning($"The user {user.Id} does not exist in the plugins config file. Skipping");
                        continue;
                    }

                    if (_userConfig.UserApiAuth != null) {
                        var auth = _userConfig.UserApiAuth.FirstOrDefault(item => item.Name == ApiName.Mal);
                        if (auth is not { AccessToken: { }, RefreshToken: { } }) {
                            _logger.LogWarning($"The user {user.Id} does not have an access or refresh token. Skipping");
                        }
                    } else {
                        _logger.LogWarning($"The user {user.Id} is not authenticated. Skipping");
                    }

                    if (LibraryCheck(e.Item) && video is Episode or Movie && e.PlayedToCompletion) {
                        _malApiCalls.UserConfig = _userConfig;
                        List<Anime> animeList = await _malApiCalls.SearchAnime(_animeType == typeof(Episode) ? episode.SeriesName : video.Name, new[] { "id", "title", "alternative_titles" });
                        bool found = false;
                        if (animeList != null) {
                            foreach (var anime in animeList) {
                                if (CompareStrings(anime.Title, _animeType == typeof(Episode) ? episode.SeriesName : movie.Name) ||
                                    CompareStrings(anime.AlternativeTitles.En, _animeType == typeof(Episode) ? episode.SeriesName : movie.Name)) {
                                    _logger.LogInformation($"Found matching {(_animeType == typeof(Episode) ? "series" : "movie")}: {anime.Title}");
                                    Anime matchingAnime = anime;
                                    if (episode?.Season.IndexNumber is > 1) {
                                        // if this is not the first season, then we need to lookup the related season.
                                        matchingAnime = await GetDifferentSeasonAnime(anime.Id, episode.Season.IndexNumber.Value);
                                        if (matchingAnime == null) {
                                            _logger.LogWarning("Could not find next season");
                                            found = true;
                                            break;
                                        }

                                        _logger.LogInformation($"Season being watched is {matchingAnime.Title}");
                                    } else if (episode?.Season.IndexNumber == 0) {
                                        // the episode is an ova or special
                                        matchingAnime = await GetOva(anime.Id, episode.Name);
                                        if (matchingAnime == null) {
                                            _logger.LogWarning("Could not find OVA");
                                            found = true;
                                            break;
                                        }
                                    }

                                    CheckUserListAnimeStatus(matchingAnime.Id, video);
                                    found = true;
                                    break;
                                }
                            }
                        }

                        if (!found) {
                            _logger.LogWarning("Series not found");
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Compare two strings, ignoring symbols and case.
        /// </summary>
        /// <param name="first">The first string.</param>
        /// <param name="second">The second string.</param>
        /// <returns>True if first string is equal to second string, false if not.</returns>
        private bool CompareStrings(string first, string second) {
            return String.Compare(first, second, CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.IgnoreSymbols) == 0;
        }

        /// <summary>
        /// Check if a string exists in another, ignoring symbols and case.
        /// </summary>
        /// <param name="first">The first string.</param>
        /// <param name="second">The second string.</param>
        /// <returns>True if first string contains second string, false if not.</returns>
        private bool ContainsExtended(string first, string second) {
            return StringFormatter.RemoveSpecialCharacters(first).Contains(StringFormatter.RemoveSpecialCharacters(second), StringComparison.OrdinalIgnoreCase);
        }

        private bool LibraryCheck(BaseItem item) {
            if (_userConfig.LibraryToCheck is { Length: > 0 }) {
                var folders = _libraryManager.GetVirtualFolders().Where(item => _userConfig.LibraryToCheck.Contains(item.ItemId));

                foreach (var folder in folders) {
                    foreach (var location in folder.Locations) {
                        if (_fileSystem.ContainsSubPath(location, item.Path)) {
                            // item is in a path of a folder the user wants to be monitored
                            return true;
                        }
                    }
                }
            } else {
                // user has no library filters
                return true;
            }

            _logger.LogInformation("Item is in a folder the user does not want to be monitored; ignoring");
            return false;
        }

        private async void CheckUserListAnimeStatus(int matchingAnimeId, Video anime) {
            Anime detectedAnime = await GetAnime(matchingAnimeId);

            if (detectedAnime == null) return;
            if (detectedAnime.MyListStatus != null && detectedAnime.MyListStatus.Status == Status.Watching) {
                _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({detectedAnime.Title}) found on watching list");
                await UpdateAnimeStatus(detectedAnime, anime.IndexNumber);
                return;
            }

            // only plan to watch
            if (_userConfig.PlanToWatchOnly) {
                if (detectedAnime.MyListStatus != null && detectedAnime.MyListStatus.Status == Status.Plan_to_watch) {
                    _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({detectedAnime.Title}) found on plan to watch list");
                    await UpdateAnimeStatus(detectedAnime, anime.IndexNumber);
                }

                // also check if rewatch completed is checked
                _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({detectedAnime.Title}) not found in plan to watch list{(_userConfig.RewatchCompleted ? ", checking completed list.." : null)}");
                CheckIfRewatchCompleted(detectedAnime, anime.IndexNumber.Value);
                return;
            }

            _logger.LogInformation("User does not have plan to watch only ticked");

            // check if rewatch completed is checked
            CheckIfRewatchCompleted(detectedAnime, anime.IndexNumber.Value);

            _logger.LogInformation("User does not have rewatch completed ticked");

            // everything else
            if (detectedAnime.MyListStatus != null) {
                // anime is on user list
                _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({detectedAnime.Title}) found on {detectedAnime.MyListStatus.Status} list");
                if (detectedAnime.MyListStatus.Status == Status.Completed) {
                    _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({detectedAnime.Title}) found on Completed list, but user does not want to automatically set as rewatching. Skipping");
                    return;
                }
            } else {
                _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({detectedAnime.Title}) not on user list");
            }

            await UpdateAnimeStatus(detectedAnime, anime.IndexNumber);
        }

        private async void CheckIfRewatchCompleted(Anime detectedAnime, int indexNumber) {
            if (_userConfig.RewatchCompleted) {
                if (detectedAnime.MyListStatus != null && detectedAnime.MyListStatus.Status == Status.Completed) {
                    _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({detectedAnime.Title}) found on completed list, setting as re-watching");
                    await UpdateAnimeStatus(detectedAnime, indexNumber, true);
                }
            } else {
                _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({detectedAnime.Title}) found on Completed list, but user does not want to automatically set as rewatching. Skipping");
            }
        }

        /// <summary>
        /// Get a single result from a user anime search.
        /// </summary>
        /// <param name="animeId">ID of the anime you want to get.</param>
        /// <param name="status">User status of the show.</param>
        /// <returns>Single anime result.</returns>
        private async Task<Anime> GetAnime(int animeId, Status? status = null) {
            Anime anime = await _malApiCalls.GetAnime(animeId, new[] {
                "id", "title", "main_picture", "alternative_titles",
                "start_date", "end_date", "synopsis", "mean", "rank", "popularity", "num_list_users",
                "num_scoring_users", "nsfw", "created_at", "updated_at", "media_type", "status", "genres", "my_list_status",
                "num_episodes", "start_season", "broadcast", "source", "average_episode_duration", "rating", "pictures",
                "background", "related_anime", "related_manga", "recommendations", "studios", "statistics"
            });
            if (anime != null && ((status != null && anime.MyListStatus != null && anime.MyListStatus.Status == status) || status == null)) {
                return anime;
            }

            return null;
        }

        /// <summary>
        /// Update a users anime status.
        /// </summary>
        /// <param name="detectedAnime">The anime search result to update.</param>
        /// <param name="episodeNumber">The episode number to update the anime to.</param>
        private async Task UpdateAnimeStatus(Anime detectedAnime, int? episodeNumber, bool? setRewatching = null) {
            if (episodeNumber != null) {
                UpdateAnimeStatusResponse response;
                if (detectedAnime.MyListStatus != null) {
                    if (detectedAnime.MyListStatus.NumEpisodesWatched < episodeNumber.Value || detectedAnime.NumEpisodes == 1) {
                        // movie or ova has only one episode, so just mark it as finished
                        if (episodeNumber.Value == detectedAnime.NumEpisodes || detectedAnime.NumEpisodes == 1) {
                            // either watched all episodes or the anime only has a single episode (ova)
                            if (detectedAnime.NumEpisodes == 1) {
                                // its a movie or ova since it only has one "episode", so the start and end date is the same
                                response = await _malApiCalls.UpdateAnimeStatus(detectedAnime.Id, 1, Status.Completed, startDate: detectedAnime.MyListStatus.IsRewatching || detectedAnime.MyListStatus.Status == Status.Completed ? null : DateTime.Now, endDate: detectedAnime.MyListStatus.IsRewatching || detectedAnime.MyListStatus.Status == Status.Completed ? null : DateTime.Now, isRewatching: false);
                            } else {
                                // user has reached the number of episodes in the anime, set as completed
                                response = await _malApiCalls.UpdateAnimeStatus(detectedAnime.Id, episodeNumber.Value, Status.Completed, endDate: detectedAnime.MyListStatus.IsRewatching || detectedAnime.MyListStatus.Status == Status.Completed ? null : DateTime.Now, isRewatching: false);
                            }

                            _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({detectedAnime.Title}) complete, marking anime as complete in MAL");
                            if (detectedAnime.MyListStatus.IsRewatching || (detectedAnime.NumEpisodes == 1 && detectedAnime.MyListStatus.Status == Status.Completed)) {
                                // also increase number of times re-watched by 1
                                // only way to get the number of times re-watched is by doing the update and capturing the response, and then re-updating :/
                                _logger.LogInformation($"{(_animeType == typeof(Episode) ? "Series" : "Movie")} ({detectedAnime.Title}) has also been re-watched, increasing re-watch count by 1");
                                response = await _malApiCalls.UpdateAnimeStatus(detectedAnime.Id, episodeNumber.Value, Status.Completed, numberOfTimesRewatched: response.NumTimesRewatched + 1, isRewatching: false);
                            }
                        } else {
                            if (detectedAnime.MyListStatus.IsRewatching) {
                                // MAL likes to mark re-watching shows as completed, instead of watching. I guess technically both are correct
                                _logger.LogInformation($"User is re-watching {(_animeType == typeof(Episode) ? "series" : "movie")} ({detectedAnime.Title}), set as completed but update re-watch progress");
                                response = await _malApiCalls.UpdateAnimeStatus(detectedAnime.Id, episodeNumber.Value, Status.Completed);
                            } else {
                                if (episodeNumber > 1) {
                                    // don't set start date after first episode
                                    response = await _malApiCalls.UpdateAnimeStatus(detectedAnime.Id, episodeNumber.Value, Status.Watching);
                                } else {
                                    _logger.LogInformation($"Setting new {(_animeType == typeof(Episode) ? "series" : "movie")} ({detectedAnime.Title}) as watching.");
                                    response = await _malApiCalls.UpdateAnimeStatus(detectedAnime.Id, episodeNumber.Value, Status.Watching, startDate: DateTime.Now);
                                }
                            }
                        }

                        if (response != null) {
                            _logger.LogInformation($"Updated {(_animeType == typeof(Episode) ? "series" : "movie")} ({detectedAnime.Title}) progress to {episodeNumber.Value}");
                        } else {
                            _logger.LogError("Could not update anime status");
                        }
                    } else {
                        if (setRewatching != null && setRewatching.Value) {
                            _logger.LogInformation($"Series ({detectedAnime.Title}) has already been watched, marking anime as re-watching");
                            response = await _malApiCalls.UpdateAnimeStatus(detectedAnime.Id, episodeNumber.Value, Status.Completed, true);
                        } else {
                            response = null;
                            _logger.LogInformation("MAL reports episode already watched; not updating");
                        }
                    }
                } else {
                    // status is not set, must be a new anime
                    _logger.LogInformation($"Adding new {(_animeType == typeof(Episode) ? "series" : "movie")} ({detectedAnime.Title}) to user list as watching with a progress of {episodeNumber.Value}");
                    response = await _malApiCalls.UpdateAnimeStatus(detectedAnime.Id, episodeNumber.Value, Status.Watching);
                }

                if (response == null) {
                    _logger.LogError("Could not update anime status");
                }
            }
        }

        /// <summary>
        /// Get further anime seasons. Jellyfin uses numbered seasons whereas MAL uses entirely different entities.
        /// </summary>
        /// <param name="animeId"></param>
        /// <param name="seasonNumber"></param>
        /// <returns></returns>
        private async Task<Anime> GetDifferentSeasonAnime(int animeId, int seasonNumber) {
            _logger.LogInformation($"Attempting to get season 1...");
            Anime initialSeason = await _malApiCalls.GetAnime(animeId, new[] { "related_anime" });

            if (initialSeason != null) {
                int i = 1;
                while (i != seasonNumber) {
                    RelatedAnime initialSeasonRelatedAnime = initialSeason.RelatedAnime.FirstOrDefault(item => item.RelationType == RelationType.Sequel);
                    if (initialSeasonRelatedAnime != null) {
                        _logger.LogInformation($"Attempting to get season {i + 1}...");
                        Anime nextSeason = await _malApiCalls.GetAnime(initialSeasonRelatedAnime.Anime.Id, new[] { "related_anime" });

                        if (nextSeason != null) {
                            initialSeason = nextSeason;
                        }
                    } else {
                        _logger.LogInformation("Could not find any related anime");
                        return null;
                    }

                    i++;
                }

                return initialSeason;
            }

            return null;
        }

        private async Task<Anime> GetOva(int animeId, string episodeName) {
            Anime anime = await _malApiCalls.GetAnime(animeId, new[] { "related_anime" });

            if (anime != null) {
                var listOfRelatedAnime = anime.RelatedAnime.Where(relation => relation.RelationType is RelationType.Side_Story or RelationType.Alternative_Version or RelationType.Alternative_Setting);
                foreach (RelatedAnime relatedAnime in listOfRelatedAnime) {
                    var detailedRelatedAnime = await _malApiCalls.GetAnime(relatedAnime.Anime.Id, new[] { "alternative_titles" });
                    if (detailedRelatedAnime != null) {
                        if (ContainsExtended(detailedRelatedAnime.Title, episodeName) || ContainsExtended(detailedRelatedAnime.AlternativeTitles.En, episodeName)) {
                            // rough match
                            return detailedRelatedAnime;
                        }
                    }
                }
            }

            // no matches
            return null;
        }


        public void Dispose() {
            _sessionManager.PlaybackStopped -= PlaybackStopped;
            GC.SuppressFinalize(this);
        }
    }
}