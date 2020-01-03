﻿using Discord;
using Discord.Commands;
using Discord.WebSocket;
using Microsoft.Extensions.DependencyInjection;
using System;
using System.Threading.Tasks;
using System.Reflection;
using Bot3PG.Data;
using Bot3PG.Modules.XP;
using Bot3PG.Modules.General;
using System.Linq;
using System.Collections.Generic;
using Bot3PG.Modules;
using Bot3PG.Data.Structs;

namespace Bot3PG.Handlers
{
    public class CommandHandler
    {
        private readonly CommandService commands;
        private readonly IServiceProvider services;
        private CommandHelp commandHelp;

        public CommandHandler(IServiceProvider services)
        {
            commands = services.GetRequiredService<CommandService>();
            this.services = services;
        }
        
        public async Task InitializeAsync()
        {
            await commands.AddModulesAsync(
                assembly: Assembly.GetEntryAssembly(),
                services: services);
            commandHelp = new CommandHelp();
        }


        public async Task HandleCommandAsync(SocketMessage socketMessage)
        {
            if (!(socketMessage is SocketUserMessage message)  || message.Author.IsWebhook || message.Channel is IPrivateChannel) return;

            var socketGuildUser = socketMessage.Author as SocketGuildUser;
            if (socketGuildUser is null) return;

            var guild = new Guild(socketGuildUser.Guild);
            try { guild = await Guilds.GetAsync(socketGuildUser.Guild); }
            catch
            {
                await socketMessage.Channel.SendMessageAsync(embed: await EmbedHandler.CreateErrorEmbed("Database", "Server configuration corrupted. Please type /reset to reset it."));
                return;
            }
            var prefix = guild?.General.CommandPrefix ?? "/";

            int position = 0;
            bool isCommand = message.HasStringPrefix(prefix, ref position);
            if (!isCommand && guild.XP.Enabled || !isCommand && message.Author.IsBot && guild.XP.BotsCanEarnEXP)
            {
                Leveling.ValidateForXPAsync(socketMessage as SocketUserMessage);
                return;
            }
            if (message.Author.IsBot) return;
            
            var context = new SocketCommandContext(Global.Client, socketMessage as SocketUserMessage);
            
            var channelIsBlacklisted = guild.General.BlacklistedChannels.Any(id => id == message.Channel.Id);
            if (channelIsBlacklisted) return;
            
            var execution = commands.ExecuteAsync(context, position, services, MultiMatchHandling.Best);

            if (!execution.Result.IsSuccess)
            {
                if (execution.Result.Error.GetType() == typeof(CommandDisabledException)) return;

                switch (execution.Result.Error)
                {
                    case CommandError.BadArgCount:
                        await context.Channel.SendMessageAsync(embed: await EmbedHandler.CreateBasicEmbed("❌ Incorrect usage", $"**Correct usage:** {CorrectCommandUsage(message, prefix)}", Color.Red));
                        break;
                    case CommandError.Exception:
                        await context.Channel.SendMessageAsync(embed: await EmbedHandler.CreateErrorEmbed("🤔 Something went wrong", $"{execution.Result.ErrorReason}"));
                        break;
                    case CommandError.ParseFailed:
                        await context.Channel.SendMessageAsync(embed: await EmbedHandler.CreateBasicEmbed("🚫 Invalid arguments", $"**Correct usage:** {CorrectCommandUsage(message, prefix)}", Color.Red));
                        break;
                    case CommandError.UnknownCommand:
                        var errorMessage = CorrectCommandUsage(message, prefix) != null ? 
                            $"**Did you mean** " + CorrectCommandUsage(message, prefix) + "?" : $"No similar commands found. Type `{prefix}help` for a list of commands.";
                        await context.Channel.SendMessageAsync(embed: await EmbedHandler.CreateBasicEmbed("❓ Unknown command", errorMessage, Color.Red));
                        break;
                    case CommandError.ObjectNotFound:
                        await context.Channel.SendMessageAsync(embed: await EmbedHandler.CreateErrorEmbed("👀 Not found", $"{execution.Result.ErrorReason}"));
                        break;
                    case CommandError.UnmetPrecondition:
                        await context.Channel.SendMessageAsync(embed: await EmbedHandler.CreateErrorEmbed("🔒 Insufficient permissions", $"**Required permissions:** {RequiredPermissions(message)}"));
                        break;
                    default: // TODO - if in debug mode
                        await context.Channel.SendMessageAsync(embed: await EmbedHandler.CreateErrorEmbed("Error", $"{execution.Exception.Message} \n**Source**: {execution.Exception.StackTrace}"));
                        break;
                }
            }
        }

        private string CorrectCommandUsage(SocketUserMessage message, string prefix)
        {
            string content = message.Content.ToLower();
            string similarCommand = commandHelp.FirstOrDefault(c => content.Contains(c.Key)).Key;
            
            string usedAlias = null;
            foreach (var command in commandHelp)
            {
                foreach (var alias in command.Value.Alias)
                {
                    if (message.Content.Split(" ")[0].Contains(alias)) usedAlias = alias;
                }
            }
            var discordCommand = Global.CommandService.Commands.FirstOrDefault(c => c.Name.ToLower() == similarCommand || c.Aliases.Contains(usedAlias));
            return discordCommand != null ? $"`{prefix}{CommandHelp.GetUsage(discordCommand, similarCommand ?? usedAlias)}`" : null;
        }

        private string RequiredPermissions(SocketUserMessage message)
        {
            foreach (var command in commandHelp)
            {
                if (message.Content.ToLower().Contains(command.Key))
                {
                    var preconditions = new List<string>();
                    foreach (var precondition in commandHelp[command.Key].Preconditions)
                    {
                        preconditions.Add(precondition.ToString());
                    }
                    return $"`{preconditions[0]}`";
                } 
            }
            return null;
        }
    }
}