﻿using Discord;
using Discord.Interactions;
using CharacterEngineDiscord.Services;
using static CharacterEngineDiscord.Services.CommonService;
using static CharacterEngineDiscord.Services.IntegrationsService;
using Microsoft.Extensions.DependencyInjection;
using Discord.WebSocket;
using CharacterEngineDiscord.Models.Common;

namespace CharacterEngineDiscord.Handlers.SlashCommands
{
    public class OtherCommands : InteractionModuleBase<InteractionContext>
    {
        //private readonly IntegrationsService _integration;
        private readonly DiscordSocketClient _client;
        //private readonly StorageContext _db;

        public OtherCommands(IServiceProvider services)
        {
            //_integration = services.GetRequiredService<IntegrationsService>();
            _client = services.GetRequiredService<DiscordSocketClient>();
            //_db = new StorageContext();
        }


        [SlashCommand("help", "All basic info about bot")]
        public async Task BaicsHelp(bool silent = true)
        {
            var embed = new EmbedBuilder().WithTitle("Sphynx AI V3").WithColor(Color.Gold)
                                          .WithDescription("**How to use**\n" +
                                                           "1. Use one of the `/spawn` commands to create a character.\n" +
                                                           "2. Modify it with one of the `/update` commands using a given prefix or webhook ID.\n" +
                                                           "3. Call character by mentioning its prefix or with reply on any of its messages.\n" +
                                                           "4. If you want to start the chat with some character from the beginning, use `/reset-character` command.\n")
                                          .AddField("API", "By default, the bot will use its owner's credentials (if those are present) for accessing all needed services like **CharacterAI** or **OpenAI**\n" +
                                                           "To use your own API keys and cAI accounts, change it with `/set-server-[ type ]-token` command.\n" +
                                                           "Each character can use different credentials.")
                                         .AddField("Also", "It's really recommended to look into `/help-messages-format`");
                                          
            await RespondAsync(embed: embed.Build(), ephemeral: silent);
        }

        [SlashCommand("help-messages-format", "Info about messages format")]
        public async Task MessagesFormatHelp(bool silent = true)
        {
            var embed = new EmbedBuilder().WithTitle("Messages format").WithColor(Color.Gold)
                                          .AddField("Description", "This setting allows you to change the format of messages that character will get from users.")
                                          .AddField("Commands", "`/show messages-format` - Check the current format of messages for this server or certain character\n" +
                                                                "`/update messages-format` - Change the format of messages for certain character\n" +
                                                                "`/set-server-messages-format` - Change the format of messages for all **new** characters on this server")
                                          .AddField("Placeholders", "You can use these placeholders in your formats to manipulate the data that being inserted in your messages:\n" +
                                                                    "**`{{msg}}`** - **Required** placeholder that contains the message itself.\n" +
                                                                    "**`{{user}}`** - Placeholder that contains the user's Discord name *(server nickname > display name > username)*.\n" +
                                                                    "**`{{ref_msg_begin}}`**, **`{{ref_msg_user}}`**, **`{{ref_msg_text}}`**, **`{{ref_msg_end}}`** - Combined placeholder that contains the referenced message (one that user was replying to). *Begin* and *end* parts are needed because user message can have no referenced message, and then placeholder will be removed.\n")
                                          .AddField("Example", "Format:\n*`{{ref_msg_begin}}((In response to '{{ref_msg_text}}' from '{{ref_msg_user}}')){{ref_msg_end}}\\n{{user}} says:\\n{{msg}}`*\n" +
                                                               "Inputs:\n- referenced message with text *`Hello`* from user *`Met`*;\n- user with name *`Lemon`*;\n- message with text *`Are you gay?`*\n" +
                                                               "Result (what character will see):\n*`((In response to 'Hello' from 'Met'))\nLemon says:\nAre you gay?`*\n" +
                                                               "Example above is used by default, but you are free to play with it the way you want, or you can simply disable it by setting the default message format with `{{msg}}`.");
            await RespondAsync(embed: embed.Build(), ephemeral: silent);
        }


        [SlashCommand("ping", "ping")]
        public async Task Ping()
        {
            await RespondAsync(embed: $":ping_pong: Pong! - {_client.Latency} ms".ToInlineEmbed(Color.Red));
        }
    }
}
