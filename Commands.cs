using System.Collections.Concurrent;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Timers;
using CounterStrikeSharp.API.Modules.Utils;
using Newtonsoft.Json.Linq;
using T3MenuSharedApi;

namespace WeaponPaints;

public partial class WeaponPaints
{
    // ...

    private void SetupKnifeMenu()
    {
        if (!Config.Additional.KnifeEnabled || !_gBCommandsAllowed) return;

        var knivesOnly = WeaponList
            .Where(pair => pair.Key.StartsWith("weapon_knife") || pair.Key.StartsWith("weapon_bayonet"))
            .ToDictionary(pair => pair.Key, pair => pair.Value);

        var giveItemMenu = Utility.CreateMenu(Localizer["wp_knife_menu_title"]);
        
        var handleGive = (CCSPlayerController player, IT3Option option) =>
        {
            if (!Utility.IsPlayerValid(player)) return;

            var playerKnives = GPlayersKnife.GetOrAdd(player.Slot, new ConcurrentDictionary<CsTeam, string>());
            var teamsToCheck = player.TeamNum < 2 
                ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist } 
                : [player.Team];
            
            var knifeName = option.OptionDisplay ?? string.Empty;

            var knifeKey = knivesOnly.FirstOrDefault(x => x.Value == knifeName).Key;
            if (string.IsNullOrEmpty(knifeKey)) return;
            if (!string.IsNullOrEmpty(Localizer["wp_knife_menu_select"]))
            {
                player.Print(Localizer["wp_knife_menu_select", knifeName]);
            }

            foreach (var team in teamsToCheck)
            {
                playerKnives[team] = knifeKey;
            }

            // Apply immediately to current knife if possible
            var activeWeapon = player.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
            if (activeWeapon != null && (activeWeapon.DesignerName.Contains("knife") || activeWeapon.DesignerName.Contains("bayonet")))
            {
                GivePlayerWeaponSkin(player, activeWeapon);
            }

            // Also apply to any knife in inventory (if not currently active)
            var myWeapons = player.PlayerPawn.Value?.WeaponServices?.MyWeapons;
            if (myWeapons != null)
            {
                foreach (var handle in myWeapons)
                {
                    var w = handle.Value;
                    if (w == null || !w.IsValid) continue;
                    if (w.DesignerName.Contains("knife") || w.DesignerName.Contains("bayonet"))
                    {
                        GivePlayerWeaponSkin(player, w);
                    }
                }
            }

            // Ensure full inventory refresh to propagate subclass change
            RefreshWeapons(player);

            // Persist knife selection to DB
            if (WeaponSync != null && player.UserId != null)
            {
                var info = new PlayerInfo
                {
                    UserId = player.UserId,
                    Slot = player.Slot,
                    Index = (int)player.Index,
                    SteamId = player.SteamID.ToString(),
                    Name = player.PlayerName,
                    IpAddress = player.IpAddress?.Split(":")[0]
                };
                var teams = teamsToCheck;
                _ = System.Threading.Tasks.Task.Run(async () => await WeaponSync.SyncKnifeToDatabase(info, knifeKey, teams));
            }
        };
        foreach (var knifePair in knivesOnly)
        {
            giveItemMenu?.AddOption(knifePair.Value, handleGive);
        }

        _config.Additional.CommandKnife.ForEach(c =>
        {
            AddCommand($"css_{c}", "Knife Menu", (player, _) =>
            {
                if (giveItemMenu == null) return;
                if (!Utility.IsPlayerValid(player) || !_gBCommandsAllowed) return;

                if (player == null || player.UserId == null) return;

                if (!CommandsCooldown.TryGetValue(player.Slot, out var cooldownEndTime) ||
                    DateTime.UtcNow >= (CommandsCooldown.TryGetValue(player.Slot, out cooldownEndTime) ? cooldownEndTime : DateTime.UtcNow))
                {
                    CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);
                    WeaponPaints.T3MenuManager?.OpenMainMenu(player, giveItemMenu);

                    return;
                }
                if (!string.IsNullOrEmpty(Localizer["wp_command_cooldown"]))
                {
                    player.Print(Localizer["wp_command_cooldown"]);
                }
            });
        });
    }

    // ...

    private void SetupSkinsMenu()
    {
        var weaponSelectionMenu = Utility.CreateMenu(Localizer["wp_skin_menu_weapon_title"]);
        var handleWeaponSelection = (CCSPlayerController? player, IT3Option option) =>
        {
            if (!Utility.IsPlayerValid(player)) return;

            var selectedWeapon = option.OptionDisplay ?? string.Empty;
            if (string.IsNullOrEmpty(selectedWeapon)) return;

            // Map from display name -> weapon key -> defindex
            var weaponKey = WeaponList.FirstOrDefault(x => x.Value == selectedWeapon).Key;
            if (string.IsNullOrEmpty(weaponKey)) return;
            var defIndex = WeaponDefindex.FirstOrDefault(x => x.Value == weaponKey).Key;
            if (defIndex == 0) return;

            // Build submenu with paints for the selected weapon
            var paintsMenu = Utility.CreateMenu($"{selectedWeapon} Skins", isSubMenu: true);
            if (paintsMenu == null) return;

            var skinsForWeapon = SkinsList
                .Where(w => w["weapon_defindex"]?.ToObject<int>() == defIndex)
                .ToList();

            foreach (var skin in skinsForWeapon)
            {
                var paintId = skin["paint"]?.ToObject<int>() ?? 0;
                var paintName = skin["paint_name"]?.ToString() ?? skin["name"]?.ToString() ?? paintId.ToString();
                if (paintId <= 0 || string.IsNullOrEmpty(paintName)) continue;

                paintsMenu.AddOption(paintName, (p, opt) =>
                {
                    if (!Utility.IsPlayerValid(p) || p is null) return;

                    var teamsToCheck = p.TeamNum < 2
                        ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist }
                        : new[] { p.Team };

                    var playerWeapons = GPlayerWeaponsInfo.GetOrAdd(p.Slot,
                        _ => new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>());

                    foreach (var team in teamsToCheck)
                    {
                        var teamWeapons = playerWeapons.GetOrAdd(team, _ => new ConcurrentDictionary<int, WeaponInfo>());
                        teamWeapons[defIndex] = new WeaponInfo
                        {
                            Paint = paintId,
                            Seed = 0,
                            Wear = 0.01f,
                            Nametag = string.Empty,
                            StatTrak = false,
                            StatTrakCount = 0,
                            KeyChain = null,
                            Stickers = new List<StickerInfo>()
                        };
                    }

                    RefreshWeapons(p);
                    var activeWeapon = p.PlayerPawn.Value?.WeaponServices?.ActiveWeapon.Value;
                    if (activeWeapon != null)
                    {
                        GivePlayerWeaponSkin(p, activeWeapon);
                    }
                    p.Print($"Applied skin: {paintName} to {selectedWeapon}");
                    paintsMenu.Close(p);

                    // Persist weapon paints to DB
                    if (WeaponSync != null && p.UserId != null)
                    {
                        var info = new PlayerInfo
                        {
                            UserId = p.UserId,
                            Slot = p.Slot,
                            Index = (int)p.Index,
                            SteamId = p.SteamID.ToString(),
                            Name = p.PlayerName,
                            IpAddress = p.IpAddress?.Split(":")[0]
                        };
                        _ = System.Threading.Tasks.Task.Run(async () => await WeaponSync.SyncWeaponPaintsToDatabase(info));
                    }
                });
            }

            paintsMenu.ParentMenu = weaponSelectionMenu;
            WeaponPaints.T3MenuManager?.OpenSubMenu(player, paintsMenu);
        };

        // ...
        foreach (var weaponName in WeaponList
                     .Where(kvp => kvp.Key != "weapon_knife")
                     .Select(kvp => kvp.Value))
        {
            weaponSelectionMenu?.AddOption(weaponName, handleWeaponSelection);
        }

        // Command to open the weapon selection menu for players
            
        _config.Additional.CommandSkinSelection.ForEach(c =>
        {
            AddCommand($"css_{c}", "Skins selection menu", (player, _) =>
            {
                if (!Utility.IsPlayerValid(player) || !_gBCommandsAllowed) return;

                if (player == null || player.UserId == null) return;

                if (!CommandsCooldown.TryGetValue(player.Slot, out var cooldownEndTime) ||
                    DateTime.UtcNow >= (CommandsCooldown.TryGetValue(player.Slot, out cooldownEndTime) ? cooldownEndTime : DateTime.UtcNow))
                {
                    CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);
                    WeaponPaints.T3MenuManager?.OpenMainMenu(player, weaponSelectionMenu);

                    return;
                }
                if (!string.IsNullOrEmpty(Localizer["wp_command_cooldown"]))
                {
                    player.Print(Localizer["wp_command_cooldown"]);
                }
            });
        });
    }

    // ...

    private void SetupGlovesMenu()
    {
        var glovesSelectionMenu = Utility.CreateMenu(Localizer["wp_glove_menu_title"]);
        if (glovesSelectionMenu == null) return;
        
        var handleGloveSelection = (CCSPlayerController? player, IT3Option option) =>
        {
            if (!Utility.IsPlayerValid(player) || player is null) return;

            var selectedPaintName = option.OptionDisplay ?? string.Empty;
            if (string.IsNullOrEmpty(selectedPaintName)) return;

            var gloveObj = GlovesList.FirstOrDefault(g => (g["paint_name"]?.ToString() ?? "") == selectedPaintName);
            if (gloveObj == null) return;

            var gloveDefIndex = gloveObj["weapon_defindex"]?.ToObject<int>() ?? 0;
            var paintId = gloveObj["paint"]?.ToObject<int>() ?? 0;
            if (gloveDefIndex == 0 || paintId == 0) return;

            var teamsToCheck = player.TeamNum < 2
                ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist }
                : new[] { player.Team };

            var playerGloves = GPlayersGlove.GetOrAdd(player.Slot, _ => new ConcurrentDictionary<CsTeam, ushort>());
            var playerWeapons = GPlayerWeaponsInfo.GetOrAdd(player.Slot,
                _ => new ConcurrentDictionary<CsTeam, ConcurrentDictionary<int, WeaponInfo>>());

            foreach (var team in teamsToCheck)
            {
                playerGloves[team] = (ushort)gloveDefIndex;
                var teamWeapons = playerWeapons.GetOrAdd(team, _ => new ConcurrentDictionary<int, WeaponInfo>());
                teamWeapons[gloveDefIndex] = new WeaponInfo
                {
                    Paint = paintId,
                    Seed = 0,
                    Wear = 0.01f,
                    Nametag = string.Empty,
                    StatTrak = false,
                    StatTrakCount = 0,
                    KeyChain = null,
                    Stickers = new List<StickerInfo>()
                };
            }

            // Apply gloves immediately
            GivePlayerGloves(player);
            player.Print($"Applied gloves: {selectedPaintName}");

            // Persist glove selection to DB
            if (WeaponSync != null && player.UserId != null)
            {
                var info = new PlayerInfo
                {
                    UserId = player.UserId,
                    Slot = player.Slot,
                    Index = (int)player.Index,
                    SteamId = player.SteamID.ToString(),
                    Name = player.PlayerName,
                    IpAddress = player.IpAddress?.Split(":")[0]
                };
                var teams = teamsToCheck;
                _ = System.Threading.Tasks.Task.Run(async () => await WeaponSync.SyncGloveToDatabase(info, (ushort)gloveDefIndex, teams));
            }
        };

        // Add weapon options to the weapon selection menu
        foreach (var paintName in GlovesList.Select(gloveObject => gloveObject["paint_name"]?.ToString() ?? "").Where(paintName => paintName.Length > 0))
        {
            glovesSelectionMenu.AddOption(paintName, handleGloveSelection);
        }

        // Command to open the weapon selection menu for players
        _config.Additional.CommandGlove.ForEach(c =>
        {
            AddCommand($"css_{c}", "Gloves selection menu", (player, info) =>
            {
                if (!Utility.IsPlayerValid(player) || !_gBCommandsAllowed) return;

                if (player == null || player.UserId == null) return;

                if (!CommandsCooldown.TryGetValue(player.Slot, out var cooldownEndTime) ||
                    DateTime.UtcNow >= (CommandsCooldown.TryGetValue(player.Slot, out cooldownEndTime) ? cooldownEndTime : DateTime.UtcNow))
                {
                    CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);
                    WeaponPaints.T3MenuManager?.OpenMainMenu(player, glovesSelectionMenu);

                    return;
                }
                if (!string.IsNullOrEmpty(Localizer["wp_command_cooldown"]))
                {
                    player.Print(Localizer["wp_command_cooldown"]);
                }
            });
        });
    }

    // ...

    private void SetupAgentsMenu()
    {
        var handleAgentSelection = (CCSPlayerController? player, IT3Option option) =>
        {
            if (!Utility.IsPlayerValid(player) || player is null) return;

            var selectedName = option.OptionDisplay ?? string.Empty;
            if (string.IsNullOrEmpty(selectedName)) return;

            var selectedAgent = AgentsList.FirstOrDefault(g =>
                g.ContainsKey("agent_name") &&
                g["agent_name"] != null && g["agent_name"]!.ToString() == selectedName &&
                g["team"] != null && (int)(g["team"]!) == player.TeamNum);

            if (selectedAgent == null) return;

            // Try common keys for the agent model identifier
            string? model = selectedAgent["model"]?.ToString()
                            ?? selectedAgent["path"]?.ToString()
                            ?? selectedAgent["vmdl"]?.ToString()
                            ?? selectedAgent["agent"]?.ToString();
            if (string.IsNullOrEmpty(model)) return;

            // Store team-specific model
            var current = GPlayersAgent.GetOrAdd(player.Slot, _ => (null, null));
            if (player.Team == CsTeam.CounterTerrorist)
                GPlayersAgent[player.Slot] = (model, current.T);
            else if (player.Team == CsTeam.Terrorist)
                GPlayersAgent[player.Slot] = (current.CT, model);
            else
                return;

            // Apply immediately
            GivePlayerAgent(player);
            player.Print($"Applied agent: {selectedName}");

            // Persist agent selection to DB
            if (WeaponSync != null && player.UserId != null)
            {
                var info = new PlayerInfo
                {
                    UserId = player.UserId,
                    Slot = player.Slot,
                    Index = (int)player.Index,
                    SteamId = player.SteamID.ToString(),
                    Name = player.PlayerName,
                    IpAddress = player.IpAddress?.Split(":")[0]
                };
                _ = System.Threading.Tasks.Task.Run(async () => await WeaponSync.SyncAgentToDatabase(info));
            }
        };

        // Command to open the weapon selection menu for players
        _config.Additional.CommandAgent.ForEach(c =>
        {
            AddCommand($"css_{c}", "Agents selection menu", (player, info) =>
            {
                if (!Utility.IsPlayerValid(player) || !_gBCommandsAllowed) return;

                if (player == null || player.UserId == null) return;

                if (!CommandsCooldown.TryGetValue(player.Slot, out DateTime cooldownEndTime) ||
                    DateTime.UtcNow >= (CommandsCooldown.TryGetValue(player.Slot, out cooldownEndTime) ? cooldownEndTime : DateTime.UtcNow))
                {
                    var agentsSelectionMenu = Utility.CreateMenu(Localizer["wp_agent_menu_title"]);
                    if (agentsSelectionMenu == null) return;

                    var filteredAgents = AgentsList.Where(agentObject =>
                    {
                        if (agentObject["team"]?.Value<int>() is { } teamNum)
                        {
                            return teamNum == player.TeamNum;
                        }
                        else
                        {
                            return false;
                        }
                    });

                    // Add weapon options to the weapon selection menu

                    foreach (var agentObject in filteredAgents)
                    {
                        var paintName = agentObject["agent_name"]?.ToString() ?? "";

                        if (paintName.Length > 0)
                            agentsSelectionMenu.AddOption(paintName, handleAgentSelection);
                    }

                    CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);
                    WeaponPaints.T3MenuManager?.OpenMainMenu(player, agentsSelectionMenu);

                    return;
                }
                if (!string.IsNullOrEmpty(Localizer["wp_command_cooldown"]))
                {
                    player.Print(Localizer["wp_command_cooldown"]);
                }
            });
        }); 
    }

    // ...

    private void SetupMusicMenu()
    {
        var musicSelectionMenu = Utility.CreateMenu(Localizer["wp_music_menu_title"]);
        if (musicSelectionMenu == null) return;

        var handleMusicSelection = (CCSPlayerController? player, IT3Option option) =>
        {
            if (!Utility.IsPlayerValid(player) || player is null) return;

            var selectedPaintName = option.OptionDisplay ?? string.Empty;
            
            var playerMusic = GPlayersMusic.GetOrAdd(player.Slot, new ConcurrentDictionary<CsTeam, ushort>());
            var teamsToCheck = player.TeamNum < 2 
                ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist } 
                : [player.Team];  // Corrected array initializer

            // ...
        };

        musicSelectionMenu.AddOption(Localizer["None"], handleMusicSelection);
        // Add weapon options to the weapon selection menu
        foreach (var paintName in MusicList.Select(musicObject => musicObject["name"]?.ToString() ?? "").Where(paintName => paintName.Length > 0))
        {
            musicSelectionMenu.AddOption(paintName, handleMusicSelection);
        }

        // Command to open the weapon selection menu for players
        _config.Additional.CommandMusic.ForEach(c =>
        {
            AddCommand($"css_{c}", "Music selection menu", (player, info) =>
            {
                if (!Utility.IsPlayerValid(player) || !_gBCommandsAllowed) return;

                if (player == null || player.UserId == null) return;

                if (!CommandsCooldown.TryGetValue(player.Slot, out var cooldownEndTime) ||
                    DateTime.UtcNow >= (CommandsCooldown.TryGetValue(player.Slot, out cooldownEndTime) ? cooldownEndTime : DateTime.UtcNow))
                {
                    CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);
                    WeaponPaints.T3MenuManager?.OpenMainMenu(player, musicSelectionMenu);

                    return;
                }
                if (!string.IsNullOrEmpty(Localizer["wp_command_cooldown"]))
                {
                    player.Print(Localizer["wp_command_cooldown"]);
                }
            });
        });
    }
    
    private void SetupPinsMenu()
    {
        var pinsSelectionMenu = Utility.CreateMenu(Localizer["wp_pins_menu_title"]);
        if (pinsSelectionMenu == null) return;

        var handlePinsSelection = (CCSPlayerController? player, IT3Option option) =>
        {
            if (!Utility.IsPlayerValid(player) || player is null) return;

            var selectedPaintName = option.OptionDisplay ?? string.Empty;

            var playerPins = GPlayersPin.GetOrAdd(player.Slot, new ConcurrentDictionary<CsTeam, ushort>());
            var teamsToCheck = player.TeamNum < 2 
                ? new[] { CsTeam.Terrorist, CsTeam.CounterTerrorist } 
                : [player.Team];

            // ...
        };

        pinsSelectionMenu.AddOption(Localizer["None"], handlePinsSelection);
        // Add weapon options to the weapon selection menu
        foreach (var paintName in PinsList.Select(musicObject => musicObject["name"]?.ToString() ?? "").Where(paintName => paintName.Length > 0))
        {
            pinsSelectionMenu.AddOption(paintName, handlePinsSelection);
        }

        // Command to open the weapon selection menu for players
        _config.Additional.CommandPin.ForEach(c =>
        {
            AddCommand($"css_{c}", "Pin selection menu", (player, info) =>
            {
                if (!Utility.IsPlayerValid(player) || !_gBCommandsAllowed) return;

                if (player == null || player.UserId == null) return;

                if (!CommandsCooldown.TryGetValue(player.Slot, out var cooldownEndTime) ||
                    DateTime.UtcNow >= (CommandsCooldown.TryGetValue(player.Slot, out cooldownEndTime) ? cooldownEndTime : DateTime.UtcNow))
                {
                    CommandsCooldown[player.Slot] = DateTime.UtcNow.AddSeconds(Config.CmdRefreshCooldownSeconds);
                    WeaponPaints.T3MenuManager?.OpenMainMenu(player, pinsSelectionMenu);

                    return;
                }
                
                if (!string.IsNullOrEmpty(Localizer["wp_command_cooldown"]))
                {
                    player.Print(Localizer["wp_command_cooldown"]);
                }
            });
        });
    }
}