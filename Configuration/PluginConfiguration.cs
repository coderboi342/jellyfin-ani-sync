using System;
using System.Collections.Generic;
using jellyfin_ani_sync.Models;
using MediaBrowser.Model.Plugins;

namespace jellyfin_ani_sync.Configuration {
    /// <summary>
    /// Plugin configuration.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration {
        public PluginConfiguration() {
            currentlyAuthenticatingUser = Guid.Empty;
        }

        /// <summary>
        /// Custom user configuration details.
        /// </summary>
        public UserConfig[] UserConfig { get; set; }

        /// <summary>
        /// Authentication details of the anime API providers.
        /// </summary>
        public ProviderApiAuth[] ProviderApiAuth { get; set; }

        /// <summary>
        /// Overriden callback URL set if the user is using Jellyfin over the internet. Generally should not be set if the user is on LAN.
        /// </summary>
        public string callbackUrl { get; set; }

        /// <summary>
        /// ID of the user that is currently authenticating. Used during the API provider callback.
        /// </summary>
        public Guid currentlyAuthenticatingUser { get; set; }
    }
}