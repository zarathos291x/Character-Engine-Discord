﻿using Discord;
using Discord.WebSocket;
using Discord.Interactions;
using CharacterEngineDiscord.Services;
using Microsoft.Extensions.DependencyInjection;
using static CharacterEngineDiscord.Services.CommonService;
using static CharacterEngineDiscord.Services.CommandsService;
using static CharacterEngineDiscord.Services.IntegrationsService;
using static CharacterEngineDiscord.Services.StorageContext;
using CharacterEngineDiscord.Models.Database;
using Discord.Webhook;
using Newtonsoft.Json.Linq;

namespace CharacterEngineDiscord.Handlers.SlashCommands
{
    [RequireManagerAccess]
    public class ManagerCommands : InteractionModuleBase<InteractionContext>
    {
        private readonly IntegrationsService _integration;
        private readonly DiscordSocketClient _client;
        private readonly StorageContext _db;

        public ManagerCommands(IServiceProvider services)
        {
            _integration = services.GetRequiredService<IntegrationsService>();
            _client = services.GetRequiredService<DiscordSocketClient>();
            _db = new StorageContext();
        }


        [SlashCommand("delete-character", "Remove character-webhook from channel")]
        public async Task DeleteCharacter(string webhookIdOrPrefix, bool silent = false)
        {
            await DeferAsync(ephemeral: silent);

            var characterWebhook = await TryToFindCharacterWebhookInChannelAsync(webhookIdOrPrefix, Context, _db);

            if (characterWebhook is null)
            {
                await FollowupAsync(embed: CHARACTER_NOT_FOUND_MESSAGE, ephemeral: silent);
                return;
            }

            if (Context.Channel is not ITextChannel textChannel)
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} Something went wrong".ToInlineEmbed(Color.Red), ephemeral: silent);
                return;
            }

            try
            {
                var discordWebhook = await textChannel.GetWebhookAsync(characterWebhook.Id);
                if (discordWebhook is not null)
                    await discordWebhook.DeleteAsync();
            }
            catch (Exception e)
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} Failed to delete webhook: `{e.Message}`".ToInlineEmbed(Color.Red), ephemeral: silent);
            }

            _db.CharacterWebhooks.Remove(characterWebhook);
            await TryToSaveDbChangesAsync(_db);

            await FollowupAsync(embed: SuccessEmbed(), ephemeral: silent);
        }

        
        public enum ClearCharactersChoise { [ChoiceDisplay("only in the current channel")]channel, [ChoiceDisplay("on the whole server")]server }
        [SlashCommand("clear-characters", "Remove all character-webhooks from this channel/server")]
        public async Task ClearCharacters(ClearCharactersChoise scope, bool silent = false)
        {
            await DeferAsync(ephemeral: silent);

            IReadOnlyCollection<IWebhook> discordWebhooks;
            List<CharacterWebhook> trackedWebhooks;

            bool all = scope is ClearCharactersChoise.server;
            if (all)
            {
                discordWebhooks = await Context.Guild.GetWebhooksAsync();
                trackedWebhooks = (from guild in (from guilds in _db.Guilds where guilds.Id == Context.Guild.Id select guilds)
                                   join channel in _db.Channels on guild.Id equals channel.Guild.Id
                                   join webhook in _db.CharacterWebhooks on channel.Id equals webhook.Channel.Id
                                   select webhook).ToList();
            }
            else
            {
                discordWebhooks = await ((SocketTextChannel)Context.Channel).GetWebhooksAsync();
                trackedWebhooks = (from channel in (from channels in _db.Channels where channels.Id == Context.Channel.Id select channels)
                                   join webhook in _db.CharacterWebhooks on channel.Id equals webhook.Channel.Id
                                   select webhook).ToList();
            }

            var trackedWebhookIds = trackedWebhooks.Select(w => w.Id).ToList();

            await Parallel.ForEachAsync(trackedWebhooks, async (tw, ct) =>
            {
                var discordWebhook = discordWebhooks.FirstOrDefault(dw => dw.Id == tw.Id);
                
                try {
                    if (discordWebhook is not null)
                        await discordWebhook.DeleteAsync();
                } finally {
                    _db.CharacterWebhooks.Remove(tw);
                }
            });

            await TryToSaveDbChangesAsync(_db);

            await FollowupAsync(embed: SuccessEmbed($"All characters {(all ? "on this server" : "in the current channel")} were removed successfully"), ephemeral: silent);
        }


        [SlashCommand("copy-character-from-channel", "-")]
        public async Task CopyCharacter(IChannel channel, string webhookIdOrPrefix, bool silent = false)
            => await CopyCharacterAsync(channel, webhookIdOrPrefix, silent);
        

        [SlashCommand("set-channel-random-reply-chance", "Set random character replies chance for this channel")]
        public async Task SetChannelRandomReplyChance(float chance, bool silent = false)
        {
            await DeferAsync(ephemeral: silent);
            
            var channel = await FindOrStartTrackingChannelAsync(Context.Channel.Id, Context.Guild.Id, _db);
            string before = channel.RandomReplyChance.ToString();
            channel.RandomReplyChance = chance;
            await TryToSaveDbChangesAsync(_db);

            await FollowupAsync(embed: SuccessEmbed($"Random reply chance for this channel was changed from {before}% to {chance}%"), ephemeral: silent);
        }

        
        [SlashCommand("hunt-user", "Make character respond on messages of certain user (or bot)")]
        public async Task HuntUser(string webhookIdOrPrefix, IUser? user = null, string? userIdOrCharacterPrefix = null, float chanceOfResponse = 100, bool silent = false)
            => await HuntUserAsync(webhookIdOrPrefix, user, userIdOrCharacterPrefix, chanceOfResponse, silent);


        [SlashCommand("stop-hunt-user", "Stop hunting user")]
        public async Task UnhuntUser(string webhookIdOrPrefix, IUser? user = null, string? userIdOrCharacterPrefix = null, bool silent = false)
            => await UnhuntUserAsync(webhookIdOrPrefix, user, userIdOrCharacterPrefix, silent);
        
        
        [SlashCommand("reset-character", "Forget all history and start chat from the beginning")]
        public async Task ResetCharacter(string webhookIdOrPrefix, bool silent = false)
        {
            await DeferAsync(ephemeral: silent);

            var characterWebhook = await TryToFindCharacterWebhookInChannelAsync(webhookIdOrPrefix, Context, _db);

            if (characterWebhook is null)
            {
                await FollowupAsync(embed: CHARACTER_NOT_FOUND_MESSAGE);
                return;
            }

            bool result = false;
            var type = characterWebhook.IntegrationType;

            if (type is IntegrationType.CharacterAI)
                result = await ResetCaiCharacterAsync(characterWebhook, silent);
            else if (type is IntegrationType.Aisekai)
                result = await ResetAisekaiCharacterAsync(characterWebhook, characterWebhook.Channel.Guild.GuildAisekaiAuthToken, silent);
            else if (type is IntegrationType.OpenAI || type is IntegrationType.KoboldAI || type is IntegrationType.HordeKoboldAI)
            {
                characterWebhook.StoredHistoryMessages.Clear();
                var firstGreetingMessage = new StoredHistoryMessage() { CharacterWebhookId = characterWebhook.Id, Content = characterWebhook.Character.Greeting, Role = "assistant" };
                await _db.StoredHistoryMessages.AddAsync(firstGreetingMessage);
                await TryToSaveDbChangesAsync(_db);
                result = true;
            }
            else
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} No API backend is set for this character".ToInlineEmbed(Color.Orange), ephemeral: silent);
            }

            if (result is false) return;

            var webhookClient = _integration.GetWebhookClient(characterWebhook.Id, characterWebhook.WebhookToken);
            if (webhookClient is null)
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} Channel webhook not found".ToInlineEmbed(Color.Red), ephemeral: silent);
                return;
            }

            string characterMessage = $"{Context.User.Mention} {characterWebhook.Character.Greeting.Replace("{{char}}", $"**{characterWebhook.Character.Name}**").Replace("{{user}}", $"**{(Context.User as SocketGuildUser)?.GetBestName()}**")}";
            if (characterMessage.Length > 2000) characterMessage = characterMessage[0..1994] + "[...]";

            try
            {
                await webhookClient.SendMessageAsync(characterMessage);
            }
            catch (Exception e)
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} Failed to send character greeting message: `{e.Message}`".ToInlineEmbed(Color.Red), ephemeral: silent);
            }
        }

        
        [SlashCommand("set-server-messages-format", "Change messages format used for all new characters on this server by default")]
        public async Task SetDefaultMessagesFormat(string newFormat, bool silent = false)
        {
            await DeferAsync(ephemeral: silent);

            var guild = await FindOrStartTrackingGuildAsync(Context.Guild.Id, _db);

            if (!newFormat.Contains("{{msg}}"))
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} Can't set format without a **`{{{{msg}}}}`** placeholder!".ToInlineEmbed(Color.Red), ephemeral: silent);
                return;
            }

            int refCount = 0;
            if (newFormat.Contains("{{ref_msg_begin}}")) refCount++;
            if (newFormat.Contains("{{ref_msg_text}}")) refCount++;
            if (newFormat.Contains("{{ref_msg_end}}")) refCount++;

            if (refCount != 0 && refCount != 3)
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} Wrong `ref_msg` placeholder format!".ToInlineEmbed(Color.Red), ephemeral: silent);
                return;
            }

            guild.GuildMessagesFormat = newFormat;
            await TryToSaveDbChangesAsync(_db);

            string text = newFormat.Replace("{{msg}}", "Hello!").Replace("{{user}}", "Lemon");

            if (refCount == 3)
            {
                text = text.Replace("{{ref_msg_text}}", "Hola")
                           .Replace("{{ref_msg_begin}}", "")
                           .Replace("{{ref_msg_end}}", "")
                           .Replace("{{ref_msg_user}}", "Met")
                           .Replace("\\n", "\n");
            }

            var embed = new EmbedBuilder().WithTitle($"{OK_SIGN_DISCORD} **Success**").WithColor(Color.Green)
                                          .AddField("New default format:", $"`{newFormat}`")
                                          .AddField("Example", $"User message: *`Hello!`*\n" +
                                                               $"User nickname: `Lemon`\n" +
                                                               $"Referenced message: *`Hola`* from user *`Met`*\n" +
                                                               $"Result (what character will see): *`{text}`*");

            await FollowupAsync(embed: embed.Build(), ephemeral: silent);
        }

        
        [SlashCommand("drop-server-messages-format", "Drop default messages format for this server")]
        public async Task DropGuildMessagesFormat(bool silent = false)
        {
            await DeferAsync(ephemeral:silent);

            var guild = await FindOrStartTrackingGuildAsync(Context.Guild.Id, _db);

            guild.GuildMessagesFormat = null;
            await TryToSaveDbChangesAsync(_db);

            await FollowupAsync(embed: SuccessEmbed(), ephemeral: silent);
        }

        
        [SlashCommand("set-server-jailbreak-prompt", "Change messages format used for all new characters on this server by default")]
        public async Task SetServerDefaultPrompt()
        {
            var modal = new ModalBuilder().WithTitle($"Update jailbreak prompt for this server")
                                          .WithCustomId($"guild~{Context.Guild.Id}")
                                          .AddTextInput("New jailbreak prompt", "new-prompt", TextInputStyle.Paragraph)
                                          .Build();
            await RespondWithModalAsync(modal);
        }

        
        [SlashCommand("drop-server-jailbreak-prompt", "Drop default jailbreak prompt for this server")]
        public async Task DropGuildPrompt(bool silent = false)
        {
            await DeferAsync(ephemeral: silent);

            var guild = await FindOrStartTrackingGuildAsync(Context.Guild.Id, _db);

            guild.GuildJailbreakPrompt = null;
            await TryToSaveDbChangesAsync(_db);

            await FollowupAsync(embed: SuccessEmbed(), ephemeral: silent);
        }


        [SlashCommand("set-server-cai-token", "Set default CharacterAI auth token for this server")]
        public async Task SetGuildCaiToken(string token, bool hasCaiPlusSubscription, bool silent = true)
        {
            await DeferAsync(ephemeral: silent);

            var guild = await FindOrStartTrackingGuildAsync(Context.Guild.Id, _db);

            guild.GuildCaiUserToken = token;
            guild.GuildCaiPlusMode = hasCaiPlusSubscription;
            await TryToSaveDbChangesAsync(_db);

            await FollowupAsync(embed: SuccessEmbed(), ephemeral: silent);
        }


        [SlashCommand("set-server-aisekai-auth", "Set default Aisekai account for this server")]
        public async Task SetGuildAisekaiAuth(string email, string password, bool silent = true)
        {
            await DeferAsync(ephemeral: silent);

            var response = await _integration.AisekaiClient.AuthorizeUserAsync(email, password);

            if (response.IsSuccessful)
            {
                var guild = await FindOrStartTrackingGuildAsync(Context.Guild.Id, _db);

                guild.GuildAisekaiAuthToken = response.ExpToken;
                guild.GuildAisekaiRefreshToken = response.RefreshToken;
                await TryToSaveDbChangesAsync(_db);

                await FollowupAsync(embed: SuccessEmbed(), ephemeral: silent);
            }
            else
            {
                await FollowupAsync(embed: $"Failed to sign in to the Aisekai account: `{response.Message}`".ToInlineEmbed(Color.Red), ephemeral: silent);
            }
        }


        [SlashCommand("set-server-openai-api", "Set default OpenAI API for this server")]
        public async Task SetGuildOpenAiToken(string token, OpenAiModel gptModel, string? reverseProxyEndpoint = null, bool silent = true)
        {
            await DeferAsync(ephemeral: silent);

            var guild = await FindOrStartTrackingGuildAsync(Context.Guild.Id, _db);

            if (reverseProxyEndpoint is not null)
                guild.GuildOpenAiApiEndpoint = reverseProxyEndpoint;

            guild.GuildOpenAiApiToken = token;
            guild.GuildOpenAiModel = gptModel is OpenAiModel.GPT_3_5_turbo ? "gpt-3.5-turbo" : gptModel is OpenAiModel.GPT_4 ? "gpt-4" : null;
            await TryToSaveDbChangesAsync(_db);

            await FollowupAsync(embed: SuccessEmbed(), ephemeral: silent);
        }


        [SlashCommand("set-server-koboldai-api", "Set default KoboldAI API for this server")]
        public async Task SetGuildKoboldAiApi(string apiEndpoint, bool silent = true)
        {
            await DeferAsync(ephemeral: silent);

            var guild = await FindOrStartTrackingGuildAsync(Context.Guild.Id, _db);

            guild.GuildKoboldAiApiEndpoint = apiEndpoint;

            await TryToSaveDbChangesAsync(_db);

            await FollowupAsync(embed: SuccessEmbed(), ephemeral: silent);
        }


        [SlashCommand("set-server-horde-koboldai-api", "Set default Horde KoboldAI API for this server")]
        public async Task SetGuildHordeKoboldAiApi(string token, string model, bool silent = true)
        {
            await DeferAsync(ephemeral: silent);

            var guild = await FindOrStartTrackingGuildAsync(Context.Guild.Id, _db);

            guild.GuildHordeApiToken = token;
            guild.GuildHordeModel = model;

            await TryToSaveDbChangesAsync(_db);

            await FollowupAsync(embed: SuccessEmbed(), ephemeral: silent);
        }


        [SlashCommand("get-horde-koboldai-workers", "Get the list of available Horde KoboldAI workers")]
        public async Task GetHordeWorkers(bool silent = true)
        {
            await DeferAsync(ephemeral: silent);

            string url = "https://horde.koboldai.net/api/v2/workers?type=text";
            using var response = await _integration.CommonHttpClient.GetAsync(url);

            Embed embed;
            if (response.IsSuccessStatusCode)
            {
                var content = await response.Content.ReadAsJsonAsync();
                int count = 1;
                if (content is not null)
                {
                    var eb = new EmbedBuilder().WithDescription("");
                    foreach (dynamic worker in (JArray)content)
                    {
                        string line = $"**{count++}. `{((JArray)worker.models).FirstOrDefault()}` by {worker.name}**" +
                                      $"Capabilities: {worker.max_length}/{worker.max_context_length} ({((string)worker.performance).Replace("tokens per second", "T/s")})\n" +
                                      $"Uptime: {worker.uptime}\n";
                        if (eb.Description.Length + line.Length > 4096) break;
                        eb.Description += line;
                    }
                    embed = eb.WithColor(Color.Green).WithTitle("Found workers:").Build();
                }
                else
                {
                    embed = new EmbedBuilder().WithColor(Color.Red).WithDescription("Something went wrong").Build();
                }
            }
            else
            {
                embed = new EmbedBuilder().WithColor(Color.Red).WithDescription("Something went wrong").Build();
            }

            await FollowupAsync(embed: embed, ephemeral: silent);
        }


        [SlashCommand("say", "Make character say something")]
        public async Task SayAsync(string webhookIdOrPrefix, string text, bool silent = false)
        {
            await DeferAsync(ephemeral: silent);

            var characterWebhook = await TryToFindCharacterWebhookInChannelAsync(webhookIdOrPrefix, Context, _db);

            if (characterWebhook is null)
            {
                await FollowupAsync(embed: CHARACTER_NOT_FOUND_MESSAGE, ephemeral: silent);
                return;
            }

            var webhookClient = _integration.GetWebhookClient(characterWebhook.Id, characterWebhook.WebhookToken);
            if (webhookClient is null)
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} Something went wrong...".ToInlineEmbed(Color.Red), ephemeral: silent);
                return;
            }

            await webhookClient.SendMessageAsync(text);
            await ModifyOriginalResponseAsync(r => r.Embed = SuccessEmbed());
        }


        [SlashCommand("block-user", "Make characters ignore certain user on this server.")]
        public async Task ServerBlockUser(IUser? user = null, string? userId = null, [Summary(description: "Don't specify hours to block forever")]int hours = 0, bool silent = false)
        {
            await DeferAsync(ephemeral: silent);

            if (user is null && userId is null)
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} Wrong user or user-ID".ToInlineEmbed(Color.Red), ephemeral: silent);
                return;
            }

            var guild = await FindOrStartTrackingGuildAsync(Context.Guild.Id);

            ulong uUserId;
            if (user is null)
            {
                bool ok = ulong.TryParse(userId, out uUserId);

                if (!ok)
                {
                    await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} Wrong user ID".ToInlineEmbed(Color.Red), ephemeral: silent);
                    return;
                }
            }
            else
            {
                uUserId = user!.Id;
            }

            if (guild.BlockedUsers.Any(bu => bu.Id == uUserId))
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} User is already blocked".ToInlineEmbed(Color.Red), ephemeral: silent);
                return;
            }

            await _db.BlockedUsers.AddAsync(new() { Id = uUserId, From = DateTime.UtcNow, Hours = hours, GuildId = guild.Id });
            await TryToSaveDbChangesAsync(_db);

            await FollowupAsync(embed: SuccessEmbed(), ephemeral: silent);
        }


        [SlashCommand("unblock-user", "---")]
        public async Task ServerUnblockUser(IUser? user = null, string? userId = null, bool silent = false)
        {
            await DeferAsync(ephemeral: silent);

            if (user is null && userId is null)
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} Wrong user or user-ID".ToInlineEmbed(Color.Red), ephemeral: silent);
                return;
            }

            ulong uUserId;
            if (user is null)
            {
                bool ok = ulong.TryParse(userId, out uUserId);

                if (!ok)
                {
                    await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} Wrong user ID".ToInlineEmbed(Color.Red), ephemeral: silent);
                    return;
                }
            }
            else
            {
                uUserId = user!.Id;
            }

            var guild = await FindOrStartTrackingGuildAsync(Context.Guild.Id);

            var blockedUser = guild.BlockedUsers.FirstOrDefault(bu => bu.Id == uUserId);
            if (blockedUser is null)
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} User not found".ToInlineEmbed(Color.Red), ephemeral: silent);
                return;
            }

            _db.BlockedUsers.Remove(blockedUser);
            await TryToSaveDbChangesAsync(_db);

            await FollowupAsync(embed: SuccessEmbed(), ephemeral: silent);
        }


        ////////////////////
        //// Long stuff ////
        ////////////////////

        private async Task<bool> ResetCaiCharacterAsync(CharacterWebhook cw, bool silent)
        {
            if (_integration.CaiClient is null)
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} CharacterAI integration is not available in the current moment".ToInlineEmbed(Color.Orange), ephemeral: silent);
                return false;
            }

            string charId = cw.CharacterId ?? string.Empty; 
            string caiToken = cw.PersonalApiToken ?? cw.Channel.Guild.GuildCaiUserToken ?? string.Empty;
            bool plusMode = cw.Channel.Guild.GuildCaiPlusMode ?? false;

            var newHisoryId = await _integration.CaiClient.CreateNewChatAsync(charId, caiToken, plusMode);
            if (newHisoryId is null)
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} Failed to create new chat with a character".ToInlineEmbed(Color.Red), ephemeral: silent);
                return false;
            }
            else
            {
                cw.ActiveHistoryID = newHisoryId;
                await TryToSaveDbChangesAsync(_db);

                await FollowupAsync(embed: SuccessEmbed(), ephemeral: silent);
                return true;
            }
        }

        private async Task<bool> ResetAisekaiCharacterAsync(CharacterWebhook characterWebhook, string? authToken, bool silent)
        {
            var guild = characterWebhook.Channel.Guild;
            
            var response = await _integration.AisekaiClient.ResetChatHistoryAsync(authToken ?? "", characterWebhook.ActiveHistoryID ?? "");
            if (response.IsSuccessful)
            {
                await FollowupAsync(embed: SuccessEmbed(), ephemeral: silent);
                characterWebhook.Character.Greeting = response.Greeting!;

                return true;
            }
            else if (response.Code == 401)
            {
                string? newAuthToken = await _integration.UpdateGuildAisekaiAuthTokenAsync(guild.Id, guild.GuildAisekaiRefreshToken ?? "");
                if (newAuthToken is null)
                {
                    await FollowupAsync(embed: ($"{WARN_SIGN_DISCORD} Failed to authorize Aisekai account`").ToInlineEmbed(Color.Red), ephemeral: silent);
                    return false;
                }

                return await ResetAisekaiCharacterAsync(characterWebhook, newAuthToken, silent);
            }
            else
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} Failed to create new chat with a character: `{response.ErrorReason}`".ToInlineEmbed(Color.Red), ephemeral: silent);
                return false;
            }
        }

        private async Task CopyCharacterAsync(IChannel channel, string webhookIdOrPrefix, bool silent)
        {
            await DeferAsync(ephemeral: silent);

            var characterWebhook = await TryToFindCharacterWebhookInChannelAsync(webhookIdOrPrefix, channel.Id, _db);

            if (characterWebhook is null)
            {
                await FollowupAsync(embed: CHARACTER_NOT_FOUND_MESSAGE, ephemeral: silent);
                return;
            }

            if (Context.Channel is not IIntegrationChannel discordChannel)
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} Failed to copy the character".ToInlineEmbed(Color.Red), ephemeral: silent);
                return;
            }

            string name = characterWebhook.Character.Name.ToLower().Contains("discord") ? characterWebhook.Character.Name.Replace('o', 'о').Replace('c', 'с') : characterWebhook.Character.Name;

            IWebhook channelWebhook;
            using (Stream? image = await TryToDownloadImageAsync(characterWebhook.Character.AvatarUrl, _integration.ImagesHttpClient))
            {                
                try { channelWebhook = await discordChannel.CreateWebhookAsync(name, image); }
                catch (Exception e)
                {
                    await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} Failed to create webhook: {e.Message}".ToInlineEmbed(Color.Red), ephemeral: silent);
                    return;
                }
            }

            try
            {
                _db.CharacterWebhooks.Add(new()
                {
                    Id = channelWebhook.Id,
                    WebhookToken = channelWebhook.Token,
                    ChannelId = (await FindOrStartTrackingChannelAsync(Context.Channel.Id, Context.Guild.Id)).Id,
                    LastCallTime = DateTime.Now,
                    MessagesSent = 1,
                    ReplyChance = 0,
                    ResponseDelay = 1,
                    FromChub = characterWebhook.FromChub,
                    CallPrefix = characterWebhook.CallPrefix,
                    CharacterId = characterWebhook.CharacterId,
                    CrutchEnabled = characterWebhook.CrutchEnabled,
                    StopBtnEnabled = characterWebhook.StopBtnEnabled,
                    IntegrationType = characterWebhook.IntegrationType,
                    PersonalMessagesFormat = characterWebhook.PersonalMessagesFormat,
                    GenerationFreqPenaltyOrRepetitionSlope = characterWebhook.GenerationFreqPenaltyOrRepetitionSlope,
                    GenerationMaxTokens = characterWebhook.GenerationMaxTokens,
                    PersonalApiModel = characterWebhook.PersonalApiModel,
                    PersonalApiToken = characterWebhook.PersonalApiToken,
                    GenerationPresenceOrRepetitionPenalty = characterWebhook.GenerationPresenceOrRepetitionPenalty,
                    GenerationTemperature = characterWebhook.GenerationTemperature,
                    ReferencesEnabled = characterWebhook.ReferencesEnabled,
                    SwipesEnabled = characterWebhook.SwipesEnabled,
                    PersonalJailbreakPrompt = characterWebhook.PersonalJailbreakPrompt,
                    PersonalApiEndpoint = characterWebhook.PersonalApiEndpoint,
                    ActiveHistoryID = null
                });

                var type = characterWebhook.IntegrationType;
                if (type is not IntegrationType.CharacterAI && type is not IntegrationType.Aisekai)
                    _db.StoredHistoryMessages.Add(new() { CharacterWebhookId = channelWebhook.Id, Content = characterWebhook.Character.Greeting, Role = "assistant" });

                await TryToSaveDbChangesAsync(_db);

                var webhookClient = new DiscordWebhookClient(channelWebhook.Id, channelWebhook.Token);
                _integration.WebhookClients.TryAdd(channelWebhook.Id, webhookClient);

                string characterMessage = $"{Context.User.Mention} {characterWebhook.Character.Greeting.Replace("{{char}}", $"**{characterWebhook.Character.Name}**").Replace("{{user}}", $"**{(Context.User as SocketGuildUser)?.GetBestName()}**")}";
                if (characterMessage.Length > 2000) characterMessage = characterMessage[0..1994] + "[...]";

                await FollowupAsync(embed: SuccessEmbed($"{characterWebhook.Character.Name} was successfully copied from {channel.Name}"), ephemeral: silent);
                await webhookClient.SendMessageAsync(characterMessage);
            }
            catch (Exception e)
            {
                LogException(new[] { e });
                TryToReportInLogsChannel(_client, "Exception", "Failed to spawn character", e.ToString(), Color.Red, true);

                await channelWebhook.DeleteAsync();
            }
        }

        private async Task HuntUserAsync(string webhookIdOrPrefix, IUser? user, string? userIdOrCharacterPrefix, float chanceOfResponse, bool silent)
        {
            await DeferAsync(ephemeral: silent);

            if (user is null && string.IsNullOrWhiteSpace(userIdOrCharacterPrefix))
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} Specify user or user ID".ToInlineEmbed(Color.Red), ephemeral: silent);
                return;
            }

            var characterWebhook = await TryToFindCharacterWebhookInChannelAsync(webhookIdOrPrefix, Context, _db);

            if (characterWebhook is null)
            {
                await FollowupAsync(embed: CHARACTER_NOT_FOUND_MESSAGE, ephemeral: silent);
                return;
            }

            string? username = user?.Mention;
            ulong? userToHuntId = user?.Id;

            if (userToHuntId is null)
            {
                bool isId = ulong.TryParse(userIdOrCharacterPrefix!.Trim(), out ulong userId);
                if (isId)
                {
                    userToHuntId = userId;
                    username = userId.ToString();
                }
                else
                {
                    var characterToHunt = await TryToFindCharacterWebhookInChannelAsync(userIdOrCharacterPrefix, Context, _db);
                    userToHuntId = characterToHunt?.Id;
                    username = characterToHunt?.Character.Name;
                }
            }

            if (userToHuntId is null)
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} User or character-webhook was not found".ToInlineEmbed(Color.Red), ephemeral: silent);
                return;
            }

            if (characterWebhook.HuntedUsers.Any(h => h.UserId == userToHuntId))
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} User is already hunted".ToInlineEmbed(Color.Orange), ephemeral: silent);
                return;
            }

            await _db.HuntedUsers.AddAsync(new() { UserId = (ulong)userToHuntId, Chance = chanceOfResponse, CharacterWebhookId = characterWebhook.Id });
            await TryToSaveDbChangesAsync(_db);

            username ??= user?.Mention;
            await FollowupAsync(embed: $":ghost: **{characterWebhook.Character.Name}** hunting **{username}**".ToInlineEmbed(Color.LighterGrey, false), ephemeral: silent);
        }

        private async Task UnhuntUserAsync(string webhookIdOrPrefix, IUser? user, string? userIdOrCharacterPrefix, bool silent)
        {
            await DeferAsync(ephemeral: silent);

            if (user is null && string.IsNullOrWhiteSpace(userIdOrCharacterPrefix))
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} Specify user or user ID".ToInlineEmbed(Color.Red), ephemeral: silent);
                return;
            }

            var characterWebhook = await TryToFindCharacterWebhookInChannelAsync(webhookIdOrPrefix, Context, _db);

            if (characterWebhook is null)
            {
                await FollowupAsync(embed: CHARACTER_NOT_FOUND_MESSAGE, ephemeral: silent);
                return;
            }

            string? username = user?.Mention;
            ulong? huntedUserId = user?.Id;

            if (huntedUserId is null)
            {
                bool isId = ulong.TryParse(userIdOrCharacterPrefix!.Trim(), out ulong userId);
                if (isId)
                {
                    huntedUserId = userId;
                    username = userId.ToString();
                }
                else
                {
                    var characterToUnhunt = await TryToFindCharacterWebhookInChannelAsync(userIdOrCharacterPrefix, Context, _db);
                    huntedUserId = characterToUnhunt?.Id;
                    username = characterToUnhunt?.Character.Name;
                }
            }

            if (huntedUserId is null)
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} User or character-webhook was not found".ToInlineEmbed(Color.Red), ephemeral: silent);
                return;
            }

            var huntedUser = characterWebhook.HuntedUsers.FirstOrDefault(h => h.UserId == huntedUserId);

            if (huntedUser is null)
            {
                await FollowupAsync(embed: $"{WARN_SIGN_DISCORD} User is not hunted".ToInlineEmbed(Color.Orange), ephemeral: silent);
                return;
            }

            characterWebhook.HuntedUsers.Remove(huntedUser);
            await TryToSaveDbChangesAsync(_db);

            username ??= user?.Mention;
            await FollowupAsync(embed: $":ghost: **{characterWebhook.Character.Name}** is not hunting **{username}** anymore".ToInlineEmbed(Color.LighterGrey, false), ephemeral: silent);
        }
    }
}
