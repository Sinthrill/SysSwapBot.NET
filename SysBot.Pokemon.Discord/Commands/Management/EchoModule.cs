﻿using Discord;
using Discord.Commands;
using Discord.WebSocket;
using SysBot.Base;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SysBot.Pokemon.Discord
{
    public class EchoModule : ModuleBase<SocketCommandContext>
    {
        private class EchoChannel
        {
            public readonly ulong ChannelID;
            public readonly string ChannelName;
            public readonly Action<string> Action;

            public EchoChannel(ulong channelId, string channelName, Action<string> action)
            {
                ChannelID = channelId;
                ChannelName = channelName;
                Action = action;
            }
        }

        private static readonly Dictionary<ulong, EchoChannel> Channels = new();
        private static readonly Dictionary<ulong, EchoChannel> ChannelsEmbed = new();

        public static void RestoreChannels(DiscordSocketClient discord, DiscordSettings cfg)
        {
            foreach (var ch in cfg.EchoChannels)
            {
                if (discord.GetChannel(ch.ID) is ISocketMessageChannel c)
                    AddEchoChannel(c, ch.ID);
            }
            foreach (var ch in cfg.EchoEmbedChannels)
            {
                if (discord.GetChannel(ch.ID) is ISocketMessageChannel c)
                    AddEchoEmbedChannel(c, ch.ID);
            }

            EchoUtil.Echo("Added echo notification to Discord channel(s) on Bot startup.");
        }

        [Command("echoHere")]
        [Summary("Makes the echo special messages to the channel.")]
        [RequireSudo]
        public async Task AddEchoAsync()
        {
            var c = Context.Channel;
            var cid = c.Id;
            if (Channels.TryGetValue(cid, out _))
            {
                await ReplyAsync("Already notifying here.").ConfigureAwait(false);
                return;
            }

            AddEchoChannel(c, cid);

            // Add to discord global loggers (saves on program close)
            SysCordSettings.Settings.EchoChannels.AddIfNew(new[] { GetReference(Context.Channel) });
            await ReplyAsync("Added Echo output to this channel!").ConfigureAwait(false);
        }

        [Command("embedHere")]
        [Summary("Echoes special embeds for cloning to the channel.")]
        [RequireSudo]
        public async Task AddEchoEmbedAsync()
        {
            var c = Context.Channel;
            var cid = c.Id;
            if (ChannelsEmbed.TryGetValue(cid, out _))
            {
                await ReplyAsync("Already notifying here.").ConfigureAwait(false);
                return;
            }

            AddEchoEmbedChannel(c, cid);

            // Add to discord global loggers (saves on program close)
            SysCordSettings.Settings.EchoEmbedChannels.AddIfNew(new[] { GetReference(Context.Channel) });
            await ReplyAsync("Added clone embed output to this channel!").ConfigureAwait(false);
        }


        private static void AddEchoChannel(ISocketMessageChannel c, ulong cid)
        {
            void Echo(string msg) => c.SendMessageAsync(msg);

            Action<string> l = Echo;
            EchoUtil.Forwarders.Add(l);
            var entry = new EchoChannel(cid, c.Name, l);
            Channels.Add(cid, entry);
        }

        private static void AddEchoEmbedChannel(ISocketMessageChannel c, ulong cid)
        {                
            void EchoEmbed(string msg) => c.SendMessageAsync("", false, EmbedBuilder.Parse(msg).Build());

            Action<string> l = EchoEmbed;
            EchoUtil.ForwardersEmbed.Add(l);
            var entry = new EchoChannel(cid, c.Name, l);
            ChannelsEmbed.Add(cid, entry);
        }

        public static bool IsEchoChannel(ISocketMessageChannel c)
        {
            var cid = c.Id;
            return Channels.TryGetValue(cid, out _);
        }

        public static bool IsEmbedChannel(ISocketMessageChannel c)
        {
            var cid = c.Id;
            return ChannelsEmbed.TryGetValue(cid, out _);
        }

        [Command("echoInfo")]
        [Summary("Dumps the special message (Echo) settings.")]
        [RequireSudo]
        public async Task DumpEchoInfoAsync()
        {
            foreach (var c in Channels)
                await ReplyAsync($"{c.Key} - {c.Value}").ConfigureAwait(false);
            foreach (var c in ChannelsEmbed)
                await ReplyAsync($"{c.Key} - {c.Value}").ConfigureAwait(false);
        }

        [Command("echoClear")]
        [Summary("Clears the special message echo settings in that specific channel.")]
        [RequireSudo]
        public async Task ClearEchosAsync()
        {
            var id = Context.Channel.Id;
            bool isEcho = Channels.TryGetValue(id, out var echo);
            bool isEmbed = ChannelsEmbed.TryGetValue(id, out var embedEcho);
            if (!isEcho && !isEmbed)
            {
                await ReplyAsync("Not echoing in this channel.").ConfigureAwait(false);
                return;
            }
            if (echo != null)
            {
                EchoUtil.Forwarders.Remove(echo.Action);
                Channels.Remove(Context.Channel.Id);
                SysCordSettings.Settings.EchoChannels.RemoveAll(z => z.ID == id);
            }
            if (embedEcho != null)
            {
                EchoUtil.ForwardersEmbed.Remove(embedEcho.Action);
                ChannelsEmbed.Remove(Context.Channel.Id);
                SysCordSettings.Settings.EchoEmbedChannels.RemoveAll(z => z.ID == id);
            }
            await ReplyAsync($"Echoes cleared from channel: {Context.Channel.Name}").ConfigureAwait(false);
        }

        [Command("echoClearAll")]
        [Summary("Clears all the special message Echo channel settings.")]
        [RequireSudo]
        public async Task ClearEchosAllAsync()
        {
            foreach (var l in Channels)
            {
                var entry = l.Value;
                await ReplyAsync($"Echoing cleared from {entry.ChannelName} ({entry.ChannelID})!").ConfigureAwait(false);
                EchoUtil.Forwarders.Remove(entry.Action);
            }
            foreach (var l in ChannelsEmbed)
            {
                var entry = l.Value;
                await ReplyAsync($"Echoing cleared from {entry.ChannelName} ({entry.ChannelID})!").ConfigureAwait(false);
                EchoUtil.ForwardersEmbed.Remove(entry.Action);
            }
            EchoUtil.Forwarders.RemoveAll(y => Channels.Select(x => x.Value.Action).Contains(y));
            EchoUtil.ForwardersEmbed.RemoveAll(y => ChannelsEmbed.Select(x => x.Value.Action).Contains(y));
            Channels.Clear();
            ChannelsEmbed.Clear();
            SysCordSettings.Settings.EchoChannels.Clear();
            SysCordSettings.Settings.EchoEmbedChannels.Clear();
            await ReplyAsync("Echoes cleared from all channels!").ConfigureAwait(false);
        }

        private RemoteControlAccess GetReference(IChannel channel) => new()
        {
            ID = channel.Id,
            Name = channel.Name,
            Comment = $"Added by {Context.User.Username} on {DateTime.Now:yyyy.MM.dd-hh:mm:ss}",
        };
    }
}