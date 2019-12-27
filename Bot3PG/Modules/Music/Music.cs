﻿using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Bot3PG.Data;
using Bot3PG.Data.Structs;
using Bot3PG.Handlers;
using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;
using Victoria;
using Victoria.Entities;
using Victoria.Queue;

namespace Bot3PG.Modules.Music
{
    [Color(45, 25, 25)]
    [RequireUserPermission(GuildPermission.Speak)]
    [RequireBotPermission(GuildPermission.Speak)]
    public sealed class Music : CommandBase
    {        
        public static AudioService AudioService { get; internal set; }

        private LavaPlayer player => AudioService.Player;
        private LavaSocketClient lavaClient => AudioService.LavaClient;
        private LavaRestClient lavaRestClient => AudioService.LavaRestClient;

        private readonly Lazy<ConcurrentDictionary<ulong, AudioOptions>> _lazyOptions = new Lazy<ConcurrentDictionary<ulong, AudioOptions>>();
        private ConcurrentDictionary<ulong, AudioOptions> Options => _lazyOptions.Value;

        [Command("Join"), Alias("J")]
        [Summary("Get bot to join your voice channel")]
        [RequireUserPermission(GuildPermission.Connect)]
        [RequireBotPermission(GuildPermission.Connect)]
        public async Task JoinAndPlay()
        {
            var user = Context.User as SocketGuildUser;
            if (user.VoiceChannel is null)
            {
                await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", $"You must be in a channel first, for {Context.Client.CurrentUser.Mention} to join.", Color.Red));
                return;
            }
            await lavaClient.MoveChannelsAsync(user.VoiceChannel);
            await lavaClient.ConnectAsync(user.VoiceChannel, Context.Channel as ITextChannel);
            
            var player = lavaClient.GetPlayer(Context.Guild.Id);
            await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", $"Now connected to `{user.VoiceChannel.Name}` and bound to `{player.TextChannel.Name}`.", Color.Blue));
        }

        [Command("Leave"), Alias("L")]
        [Summary("Get bot to leave your voice channel")]
        [RequireUserPermission(GuildPermission.Connect)]
        [RequireBotPermission(GuildPermission.Connect)]
        public async Task Leave()
        {
            CurrentGuild ??= await Guilds.GetAsync(Context.Guild);
            string prefix = CurrentGuild.General.CommandPrefix;

            try
            {
                var player = lavaClient.GetPlayer(Context.Guild.Id);
                if (player is null)
                {
                    await SendNoPlayerPrompt();
                    return;
                }                
                else if (player.IsPlaying)
                {
                    await player.StopAsync();
                }                
                var channel = player.VoiceChannel;
                await lavaClient.DisconnectAsync(channel);
                await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", $"I've left `{channel.Name}`. Thank you for playing music 🎵.", Color.Blue));
            }
            catch (InvalidOperationException ex)
            {
                await ReplyAsync(EmbedHandler.CreateErrorEmbed("Music", ex.ToString()));
                return;
            }
        }

        [Command("Play"), Alias("YouTube", "P")]
        [Summary("Search YouTube for tracks to play")]
        [RequireUserPermission(GuildPermission.Speak)]
        [RequireBotPermission(GuildPermission.Speak), RequireBotPermission(GuildPermission.Connect)]
        public async Task Play([Remainder]string query)
        {
            var user = Context.User as SocketGuildUser;
            if (user.VoiceChannel is null) 
            {
                await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", "Please join a channel first", Color.Red));
                return;
            }
            try
            {
                //Options.TryAdd(user.Guild.Id, new AudioOptions { Summoner = user });
                await lavaClient.ConnectAsync(user.VoiceChannel, Context.Channel as ITextChannel);
                await lavaClient.MoveChannelsAsync(user.VoiceChannel);

                var player = lavaClient.GetPlayer(Context.Guild.Id);

                query = query.Replace("https://www.youtube.com/watch?v=", "");
                var search = await lavaRestClient.SearchYouTubeAsync(query);
                if (search.LoadType == LoadType.NoMatches) 
                {
                    await ReplyAsync(EmbedHandler.CreateErrorEmbed("Music", $"OOF! I wasn't able to find anything for '{query}'."));
                    return;
                }

                var track = search.Tracks.FirstOrDefault();
                if (track is null)
                {
                    await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", "No results found.", Color.Red));
                    return;
                }
                else if (track.Length > TimeSpan.FromHours(CurrentGuild.Music.MaxTrackHours)) 
                {
                    await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", $"Track duration must be less than {CurrentGuild.Music.MaxTrackHours} hours.", Color.Red));
                    return;
                }

                if (player.CurrentTrack != null || player.IsPaused)
                {
                    player.Queue.Enqueue(track);
                    await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", $"{Hyperlink(track)} has been added to queue.", Color.Blue));
                }
                else
                {
                    await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", $"**Now playing**: {Hyperlink(track)}", Color.Blue));
                    await player.PlayAsync(track);
                    await player.SetVolumeAsync(CurrentGuild.Music.DefaultVolume);
                }
            }
            catch (Exception ex) { await ReplyAsync(EmbedHandler.CreateErrorEmbed("Music", ex.ToString())); }
        }

        [Command("Stop"), Alias("S")]
        [Summary("Stop all music playback")]
        [RequireUserPermission(GuildPermission.Speak)]
        [RequireBotPermission(GuildPermission.Speak)]
        public async Task Stop()
        {
            try
            {
                var player = lavaClient.GetPlayer(Context.Guild.Id);
                if (player is null)
                {
                    await SendNoPlayerPrompt();
                    return;
                }
                if (player.IsPlaying)
                {
                    await player.StopAsync();
                }
                foreach (var track in player.Queue.Items)
                {
                    player.Queue.Dequeue();
                }
                await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", "I've stopped playback and the playlist has been cleared.", Color.Blue));
            }
            catch (Exception ex) { await ReplyAsync(EmbedHandler.CreateErrorEmbed("Music", ex.ToString())); }
        }

        [Command("Queue"), Alias("List", "Playlist", "Q")]
        [Summary("Display currently playing or listed tracks")]
        public async Task Queue()
        {
            try
            {
                var player = lavaClient.GetPlayer(Context.Guild.Id);
                if (player is null)
                {
                    await SendNoPlayerPrompt();
                    return;
                }
                else if (player.IsPlaying)
                {
                    if (player.Queue.Count < 1 && player.CurrentTrack != null) 
                    {
                        await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", 
                            $"**Now Playing**: {Hyperlink(player.CurrentTrack)}\n\nNothing else is queued.", Color.Blue));
                        return;
                    }
                    
                    string description = "";
                    int trackNum = 2;
                    foreach (LavaTrack track in player.Queue.Items)
                    {
                        description += $"**[{trackNum}]**: {Hyperlink(track)}\n";
                        trackNum++;
                    }
                    await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", 
                        $"**Now Playing**: {Hyperlink(player.CurrentTrack)}\n\n{description}", Color.Blue));
                    return;
                }
                await ReplyAsync(EmbedHandler.CreateErrorEmbed("Music", "Player doesn't seem to be playing anything right now."));
            }
            catch {}
        }

        [Command("Skip"), Alias("Next")]
        [Summary("Play next track in queue")]
        [RequireUserPermission(GuildPermission.Speak)]
        public async Task Skip(int count = 1)
        {
            CurrentGuild ??= await Guilds.GetAsync(Context.Guild);
            string prefix = CurrentGuild.General.CommandPrefix;
            
            var player = lavaClient.GetPlayer(Context.Guild.Id);
            if (player is null) 
            {
                await SendNoPlayerPrompt();
                return;
            }
            else if (player.Queue.Count < 1) 
            {
                await SendNotPlayingPrompt("skip", prefix);
                return;
            }
            else if (player.Queue.Count < count)
            {
                await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", "Tracks to skip must be less than queue size.", Color.Red));
                return;                    
            }
            /*if (CurrentGuild.Music.VoteToSkip)
            {
                var channelMembers = player.VoiceChannel.GetUsersAsync();
                int memberCount = await channelMembers.Count();
                int votes = 0;
                votes++;
                
                bool hasEnoughVotes = CurrentGuild.Music.AllVotesToSkip ? votes == memberCount : votes >= (memberCount / 2);
                if (!hasEnoughVotes)
                {
                    string requirement = CurrentGuild.Music.AllVotesToSkip ? "Every channel member is" : "At least 50% of members are";
                    await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", $"{requirement} required to vote to skip this track", Color.Blue));
                    return;
                }
            }*/
            try
            {
                await ReplyAsync($"Pre-skip: {player.Queue.Items.Count()} tracks");
                var oldTrack = await player.SkipAsync();
                await ReplyAsync($"Post-skip: {player.Queue.Items.Count()} tracks");
                await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", $"{Hyperlink(oldTrack)} successfully skipped", Color.Blue));
            }
            catch (Exception ex) { await ReplyAsync(EmbedHandler.CreateErrorEmbed("Music", ex.ToString())); }
        }

        [Command("Volume"), Alias("V")]
        [Summary("Manage the volume of bot music")]
        [RequireUserPermission(GuildPermission.Speak)]
        public async Task Volume(int volume = 0)
        {
            if (volume < 0 || volume > 200) 
            {
                await ReplyAsync(EmbedHandler.CreateErrorEmbed("Music", "Volume must be between 0 and 200."));
                return;
            }
            try
            {
                var player = lavaClient.GetPlayer(Context.Guild.Id);
                if (player is null) 
                {
                    await SendNoPlayerPrompt();
                    return;
                }
                if (volume == 0) 
                {
                    await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", $"**Current Volume:** {player.CurrentVolume}", Color.Blue));
                    return;
                }
                await player.SetVolumeAsync(volume);
                await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", $"Volume has been set to `{volume}`.", Color.Blue));
            }
            catch (InvalidOperationException ex) { await ReplyAsync(EmbedHandler.CreateErrorEmbed("Music", ex.ToString())); }
        }

        [Command("Replay"), Alias("Position", "Seek")]
        [Summary("Toggle playback, if playing")]
        [RequireUserPermission(GuildPermission.Speak)]
        public async Task Replay(int seconds = -1)
        {
            CurrentGuild ??= await Guilds.GetAsync(Context.Guild);
            string prefix = CurrentGuild.General.CommandPrefix;

            var player = lavaClient.GetPlayer(Context.Guild.Id);
            if (player is null) 
            {
                await SendNoPlayerPrompt();
                return;
            }
            if (player.CurrentTrack is null)
            {
                await SendNotPlayingPrompt("Replay", prefix);
                return;
            }

            int totalSeconds = (int)player.CurrentTrack.Length.TotalSeconds;
            if (seconds == -1)
            {
                await ReplyAsync(embed: EmbedHandler.CreateBasicEmbed("Music", $"{Hyperlink(player.CurrentTrack)} is `{totalSeconds}` seconds long.", Color.Blue));
                return;
            }
            if (seconds < 0 || seconds > player.CurrentTrack.Length.TotalSeconds)
            {
                await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", 
                    $"Track length must be between `0` seconds and `{totalSeconds}`.", Color.Red));
                return;
            }
            await player.SeekAsync(TimeSpan.FromSeconds(seconds));
        }

        [Command("Pause")]
        [Summary("Toggle playback, if playing")]
        [RequireUserPermission(GuildPermission.Speak)]
        public async Task Pause()
        {            
            try
            {
                var player = lavaClient.GetPlayer(Context.Guild.Id);
                if (player is null)
                {
                    await SendNoPlayerPrompt();
                }
                else if (!player.IsPaused)
                {
                    await player.PauseAsync();
                    await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", $"**Paused:** {player.CurrentTrack.Title}", Color.Blue));
                }
                else
                {
                    await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", $"Track already paused.", Color.Red));
                }
            }
            catch (InvalidOperationException ex) { await ReplyAsync(EmbedHandler.CreateErrorEmbed("Music", ex.Message)); }
        }
        
        [Command("Pause"), Alias("Resume")]
        [Summary("Toggle playback, if playing")]
        [RequireUserPermission(GuildPermission.Speak)]
        public async Task Resume()
        {            
            try
            {
                var player = lavaClient.GetPlayer(Context.Guild.Id);
                if (player is null)
                {
                    await SendNoPlayerPrompt();
                }
                else if (player.IsPaused)
                {
                    await player.ResumeAsync();
                    await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", $"**Resumed:** Now Playing {player.CurrentTrack.Title}.", Color.Blue));
                }
                else
                {
                    await ReplyAsync(EmbedHandler.CreateBasicEmbed("Music", $"Track already resumed.", Color.Red));
                }
            }
            catch (InvalidOperationException ex) { await ReplyAsync(EmbedHandler.CreateErrorEmbed("Music", ex.Message)); }
        }

        private async Task SendNoPlayerPrompt()
        {
            var guild = await Guilds.GetAsync(Context.Guild);
            string prefix = CurrentGuild.General.CommandPrefix;
            await ReplyAsync(EmbedHandler.CreateErrorEmbed("Music", 
                $"Could not aquire player.\nAre you using {Context.Client.CurrentUser.Mention} right now? Type `{prefix}help` for more info."));
        }

        private async Task SendNotPlayingPrompt(string action, string prefix) 
            => await ReplyAsync(EmbedHandler.CreateErrorEmbed("Music", $"Unable to {action} as there are one or no songs playing.\nDid you mean `{prefix}stop`?"));

        private static string GetDuration(LavaTrack track) => track.Length.ToString(track.Length > TimeSpan.FromHours(1) ? @"hh\:mm\:ss" : @"mm\:ss");
        public static string Hyperlink(LavaTrack track) => $"[{track.Title}]({track.Uri}) `{GetDuration(track)}`";
    }
}