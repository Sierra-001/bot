namespace Miki.Modules.Accounts
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Text.RegularExpressions;
    using System.Threading.Tasks;
    using Api.Models;
    using Framework.Extension;
    using Localization.Models;
    using Microsoft.EntityFrameworkCore;
    using Microsoft.Extensions.DependencyInjection;
    using Miki.Accounts;
    using Miki.API;
    using Miki.API.Leaderboards;
    using Miki.Bot.Models;
    using Miki.Bot.Models.Exceptions;
    using Miki.Bot.Models.Repositories;
    using Miki.Cache;
    using Miki.Common.Builders;
    using Miki.Discord;
    using Miki.Discord.Common;
    using Miki.Discord.Rest;
    using Miki.Exceptions;
    using Miki.Localization;
    using Miki.Framework;
    using Miki.Framework.Commands;
    using Miki.Framework.Commands.Attributes;
    using Miki.Helpers;
    using Miki.Logging;
    using Miki.Models.Objects.Backgrounds;
    using Miki.Modules.Accounts.Services;
    using Miki.Services.Achievements;

    [Module("Accounts")]
    public class AccountsModule
    {
        public AchievementService  AchievementService { get; set; }
        public AchievementLoader Achievements { get; set; }
        public ExperienceTrackerService ExperienceService { get; set; }

        private readonly Net.Http.HttpClient client;

        private readonly EmojiBarSet onBarSet = new EmojiBarSet(
            "<:mbarlefton:391971424442646534>",
            "<:mbarmidon:391971424920797185>",
            "<:mbarrighton:391971424488783875>");

        private readonly EmojiBarSet offBarSet = new EmojiBarSet(
            "<:mbarleftoff:391971424824459265>",
            "<:mbarmidoff:391971424824197123>",
            "<:mbarrightoff:391971424862208000>");

        public AccountsModule(MikiApp app)
        {
            var config = app.Services.GetService<Config>();

            if (!string.IsNullOrWhiteSpace(config.MikiApiKey)
                && !string.IsNullOrWhiteSpace(config.ImageApiUrl))
            {
                client = new Net.Http.HttpClient(config.ImageApiUrl)
                    .AddHeader("Authorization", config.MikiApiKey);
            }
            else
            {
                Log.Warning("Image API can not be loaded in AccountsModule");
            }

            var accountsService = app.Services.GetService<AccountService>();
            accountsService.OnLocalLevelUp += OnUserLevelUp;
            accountsService.OnLocalLevelUp += OnLevelUpAchievements;

            AchievementService = app.Services.GetService<AchievementService>();
            Achievements = new AchievementLoader(AchievementService);


            var service = app.Services.GetService<AchievementService>();
            service.OnAchievementUnlocked += this.OnAchievementUnlocked;

            ExperienceService = new ExperienceTrackerService(
                app.Services.GetService<IDiscordClient>(),
                accountsService);
        }

        private async Task OnLevelUpAchievements(IDiscordUser user, IDiscordTextChannel channel, int level)
        {
            var achievements = AchievementService.GetAchievement("levelachievements");

            int achievementToUnlock = -1;
            if(level >= 3 && level < 5)
            {
                achievementToUnlock = 0;
            }
            else if(level >= 5 && level < 10)
            {
                achievementToUnlock = 1;
            }
            else if(level >= 10 && level < 20)
            {
                achievementToUnlock = 2;
            }
            else if(level >= 20 && level < 30)
            {
                achievementToUnlock = 3;
            }
            else if(level >= 30 && level < 50)
            {
                achievementToUnlock = 4;
            }
            else if(level >= 50 && level < 100)
            {
                achievementToUnlock = 5;
            }
            else if (level >= 100 && level < 150)
            {
                achievementToUnlock = 6;
            }
            else if (level >= 150)
            {
                achievementToUnlock = 7;
            }

            if(achievementToUnlock != -1)
            {
                if (MikiApp.Instance is MikiBotApp instance)
                {
                    await AchievementService.UnlockAsync(
                        await instance.CreateFromUserChannelAsync(user, channel),
                        achievements,
                        user.Id,
                        achievementToUnlock);
                }
            }
        }

        /// <summary>
        /// Notification for local user level ups.
        /// </summary>
        private async Task OnUserLevelUp(IDiscordUser user, IDiscordTextChannel channel, int level)
        {
            using var scope = MikiApp.Instance.Services.CreateScope();
            var context = scope.ServiceProvider.GetService<MikiDbContext>();
            
            var service = scope.ServiceProvider
                .GetService<ILocalizationService>();

            Locale instance = await service.GetLocaleAsync((long)channel.Id)
                .ConfigureAwait(false);

            EmbedBuilder embed = new EmbedBuilder
            {
                Title = instance.GetString("miki_accounts_level_up_header"),
                Description = instance.GetString(
                    "miki_accounts_level_up_content",
                    $"{user.Username}#{user.Discriminator}",
                    level),
                Color = new Color(1, 0.7f, 0.2f)
            };

            if(channel is IDiscordGuildChannel guildChannel)
            {
                IDiscordGuild guild = await guildChannel.GetGuildAsync()
                    .ConfigureAwait(false);
                long guildId = guild.Id.ToDbLong();

                List<LevelRole> rolesObtained = await context.LevelRoles
                    .Where(p => p.GuildId == guildId 
                                && p.RequiredLevel == level 
                                && p.Automatic)
                    .ToListAsync()
                    .ConfigureAwait(false);

                var notificationSetting = await Setting.GetAsync(
                        context, channel.Id, DatabaseSettingId.LevelUps)
                    .ConfigureAwait(false);

                switch((LevelNotificationsSetting)notificationSetting)
                {
                    case LevelNotificationsSetting.NONE:
                    case LevelNotificationsSetting.RewardsOnly when rolesObtained.Count == 0:
                        return;
                    case LevelNotificationsSetting.All:
                        break;

                    default:
                        throw new InvalidOperationException();
                }

                if(rolesObtained.Count > 0)
                {
                    List<IDiscordRole> roles = (await guild.GetRolesAsync().ConfigureAwait(false))
                        .ToList();

                    IDiscordGuildUser guildUser = await guild.GetMemberAsync(user.Id)
                        .ConfigureAwait(false);
                    if(guildUser != null)
                    {
                        foreach(LevelRole role in rolesObtained)
                        {
                            IDiscordRole r = roles.FirstOrDefault(x => x.Id == (ulong)role.RoleId);
                            if(r == null)
                            {
                                continue;
                            }

                            await guildUser.AddRoleAsync(r)
                                .ConfigureAwait(false);
                        }
                    }

                    var rewards = string.Join("\n", rolesObtained
                        .Select(x => $"New Role: **{roles.FirstOrDefault(z => z.Id.ToDbLong() == x.RoleId)?.Name}**"));

                    embed.AddInlineField("Rewards", rewards);
                }
            }

            await embed.ToEmbed()
                .QueueAsync(
                    scope.ServiceProvider.GetService<MessageWorker>(),
                    scope.ServiceProvider.GetService<IDiscordClient>(),
                    channel)
                .ConfigureAwait(false);
        }

        /// <summary>
        /// Notification for user achievements
        /// </summary>
        private Task OnAchievementUnlocked(IContext ctx, AchievementEntry arg)
        {
            return new EmbedBuilder()
                .SetTitle($"{arg.Icon} Achievement Unlocked!")
                .SetDescription($"{ctx.GetAuthor().Username} has unlocked {arg.ResourceName}")
                .ToEmbed()
                .QueueAsync(ctx, ctx.GetChannel());
        }

        [Command("achievements")]
        public async Task AchievementsAsync(IContext e)
        {
            var context = e.GetService<MikiDbContext>();
            var locale = e.GetLocale();

            long id = (long)e.GetAuthor().Id;

            if(e.GetArgumentPack().Take(out string arg))
            {
                IDiscordUser user = await DiscordExtensions.GetUserAsync(arg, e.GetGuild());

                if(user != null)
                {
                    id = (long)user.Id;
                }
            }

            IDiscordUser discordUser = await e.GetGuild().GetMemberAsync(id.FromDbLong());
            User u = await User.GetAsync(context, discordUser.Id, discordUser.Username);

            List<Achievement> achievements = await context.Achievements
                .Where(x => x.UserId == id)
                .ToListAsync();

            EmbedBuilder embed = new EmbedBuilder()
                .SetAuthor($"{u.Name} | " + "Achievements", discordUser.GetAvatarUrl(), "https://miki.ai/profiles/ID/achievements");

            embed.SetColor(255, 255, 255);

            StringBuilder leftBuilder = new StringBuilder();

            int totalScore = 0;
            var achievementService = e.GetService<AchievementService>();

            foreach(var a in achievements)
            {
                AchievementEntry metadata = achievementService.GetAchievement(a.Name).Entries[a.Rank];
                // TODO: Clean up or turn into resource.
                leftBuilder.AppendLine(
                    metadata.Icon + " | `" + metadata.ResourceName.PadRight(15) 
                    + $"{metadata.Points.ToString().PadLeft(3)} pts`" 
                    + " | 📅 {a.UnlockedAt.ToShortDateString()}");
                totalScore += metadata.Points;
            }

            embed.AddInlineField(
                "Total Pts: " + totalScore.ToFormattedString(),
                string.IsNullOrEmpty(leftBuilder.ToString())
                    ? locale.GetString("miki_placeholder_null")
                    : leftBuilder.ToString());

            await embed.ToEmbed().QueueAsync(e, e.GetChannel());
        }

        [Command("exp")]
        public async Task ExpAsync(IContext e)
        {
            Stream s = await client.GetStreamAsync("api/user?id=" + e.GetMessage().Author.Id);
            if(s == null)
            {
                await e.ErrorEmbed("Image generation API did not respond. This is an issue, please report it.")
                    .ToEmbed().QueueAsync(e, e.GetChannel());
                return;
            }
            e.GetChannel()
                .QueueMessage(e, stream: s);
        }

        [Command("leaderboards", "lb", "leaderboard", "top")]
        public async Task LeaderboardsAsync(IContext e)
        {
            LeaderboardsOptions options = new LeaderboardsOptions();

            e.GetArgumentPack().Peek(out string argument);

            switch(argument?.ToLower() ?? "")
            {
                case "commands":
                case "cmds":
                {
                    options.Type = LeaderboardsType.COMMANDS;
                    e.GetArgumentPack().Skip();
                }
                break;

                case "currency":
                case "mekos":
                case "money":
                case "bal":
                {
                    options.Type = LeaderboardsType.CURRENCY;
                    e.GetArgumentPack().Skip();
                }
                break;

                case "rep":
                case "reputation":
                {
                    options.Type = LeaderboardsType.REPUTATION;
                    e.GetArgumentPack().Skip();
                }
                break;

                case "pasta":
                case "pastas":
                {
                    options.Type = LeaderboardsType.PASTA;
                    e.GetArgumentPack().Skip();
                }
                break;

                case "experience":
                case "exp":
                {
                    options.Type = LeaderboardsType.EXPERIENCE;
                    e.GetArgumentPack().Skip();
                }
                break;

                case "guild":
                case "guilds":
                {
                    options.Type = LeaderboardsType.GUILDS;
                    e.GetArgumentPack().Skip();
                }
                break;

                default:
                {
                    options.Type = LeaderboardsType.EXPERIENCE;
                }
                break;
            }

            if(e.GetArgumentPack().Peek(out string localArg))
            {
                if(localArg.ToLower() == "local")
                {
                    if(options.Type != LeaderboardsType.PASTA)
                    {
                        options.GuildId = e.GetGuild().Id;
                    }
                    e.GetArgumentPack().Skip();
                }
            }

            if(e.GetArgumentPack().Peek(out int index))
            {
                options.Offset = Math.Max(0, index - 1) * 12;
                e.GetArgumentPack().Skip();
            }

            options.Amount = 12;

            var api = e.GetService<MikiApiClient>();

            LeaderboardsObject obj = await api.GetPagedLeaderboardsAsync(options);

            await Utils.RenderLeaderboards(new EmbedBuilder(), obj.Items, obj.CurrentPage * 12)
                .SetFooter(
                    e.GetLocale().GetString(
                        "page_index", 
                        obj.CurrentPage + 1, 
                        Math.Ceiling((double)obj.TotalPages / 10)))
                .SetAuthor(
                    "Leaderboards: " + options.Type + " (click me!)",
                    null,
                    api.BuildLeaderboardsUrl(options)
                )
                .ToEmbed()
                .QueueAsync(e, e.GetChannel());
        }

        [Command("profile")]
        public async Task ProfileAsync(IContext e)
        {
            var args = e.GetArgumentPack();
            var locale = e.GetLocale();

            var context = e.GetService<MikiDbContext>();

            IDiscordGuildUser self = await e.GetGuild().GetSelfAsync();

            IDiscordUser discordUser;
            if(args.Take(out string arg))
            {
                discordUser = await DiscordExtensions.GetUserAsync(arg, e.GetGuild());
                if(discordUser == null)
                {
                    throw new UserNullException();
                }
            }
            else
            {
                discordUser = e.GetAuthor();
            }

            User account = await User.GetAsync(
                context, discordUser.Id.ToDbLong(), discordUser.Username);
            if(account == null)
            {
                throw new UserNullException();
            }

            string icon = null;
            if(await account.IsDonatorAsync(context))
            {
                icon = "https://cdn.discordapp.com/emojis/421969679561785354.png";
            }

            EmbedBuilder embed = new EmbedBuilder()
                .SetDescription(account.Title)
                .SetAuthor(locale.GetString("miki_global_profile_user_header", discordUser.Username),
                    icon, "https://patreon.com/mikibot")
                .SetThumbnail(discordUser.GetAvatarUrl());

            var infoValueBuilder = new MessageBuilder();
            if(e.GetGuild() != null)
            {
                LocalExperience localExp = await LocalExperience.GetAsync(
                    context,
                    e.GetGuild().Id,
                    discordUser.Id);
                if(localExp == null)
                {
                    localExp = await LocalExperience.CreateAsync(
                        context,
                        e.GetGuild().Id,
                        discordUser.Id,
                        discordUser.Username);
                }

                int rank = await localExp.GetRankAsync(context);
                int localLevel = User.CalculateLevel(localExp.Experience);
                int maxLocalExp = User.CalculateLevelExperience(localLevel);
                int minLocalExp = User.CalculateLevelExperience(localLevel - 1);

                EmojiBar expBar = new EmojiBar(maxLocalExp - minLocalExp, onBarSet, offBarSet, 6);
                infoValueBuilder.AppendText(e.GetLocale().GetString(
                    "miki_module_accounts_information_level",
                    localLevel,
                    localExp.Experience.ToFormattedString(),
                    maxLocalExp.ToFormattedString()));

                if(await self.HasPermissionsAsync(GuildPermission.UseExternalEmojis))
                {
                    infoValueBuilder.AppendText(
                        expBar.Print(localExp.Experience - minLocalExp));
                }

                infoValueBuilder.AppendText(locale.GetString(
                    "miki_module_accounts_information_rank",
                    rank.ToFormattedString()));
            }
            infoValueBuilder.AppendText(
                $"Reputation: {account.Reputation:N0}",
                newLine: false);

            embed.AddInlineField(locale.GetString("miki_generic_information"), infoValueBuilder.Build());

            int globalLevel = User.CalculateLevel(account.Total_Experience);
            int maxGlobalExp = User.CalculateLevelExperience(globalLevel);
            int minGlobalExp = User.CalculateLevelExperience(globalLevel - 1);

            int? globalRank = await account.GetGlobalRankAsync(context);

            EmojiBar globalExpBar = new EmojiBar(maxGlobalExp - minGlobalExp, onBarSet, offBarSet, 6);

            var globalInfoBuilder = new MessageBuilder()
                .AppendText(locale.GetString(
                    "miki_module_accounts_information_level",
                    globalLevel.ToFormattedString(),
                    account.Total_Experience.ToFormattedString(),
                    maxGlobalExp.ToFormattedString()));
            if(await self.HasPermissionsAsync(GuildPermission.UseExternalEmojis))
            {
                globalInfoBuilder.AppendText(
                    globalExpBar.Print(maxGlobalExp - minGlobalExp));
            }

            var globalInfo = globalInfoBuilder
                .AppendText(
                    locale.GetString("miki_module_accounts_information_rank",
                        globalRank?.ToFormattedString() ?? "We haven't calculated your rank yet!"),
                        MessageFormatting.Plain,
                        false)
                .Build();

            embed.AddInlineField(
                locale.GetString("miki_generic_global_information"),
                globalInfo);

            embed.AddInlineField(
                locale.GetString("miki_generic_mekos"),
                $"{account.Currency:N0} <:mekos:421972155484471296>");

            MarriageRepository repository = new MarriageRepository(context);
            List<UserMarriedTo> marriages = (await repository.GetMarriagesAsync((long)discordUser.Id))
                .Where(x => !x.Marriage.IsProposing)
                .ToList();

            List<string> users = new List<string>();

            int maxCount = marriages.Count;

            for(int i = 0; i < maxCount; i++)
            {
                users.Add((await e.GetService<DiscordClient>()
                    .GetUserAsync(marriages[i].GetOther((long)discordUser.Id).FromDbLong())).Username);
            }

            if(marriages.Count > 0)
            {
                List<string> marriageStrings = new List<string>();

                for(int i = 0; i < maxCount; i++)
                {
                    if(marriages[i].GetOther((long)discordUser.Id) == 0)
                    {
                        continue;
                    }
                    marriageStrings.Add(
                        $"💕 {users[i]} (_{marriages[i].Marriage.TimeOfMarriage.ToShortDateString()}_)");
                }

                string marriageText = string.Join("\n", marriageStrings);
                if(string.IsNullOrEmpty(marriageText))
                {
                    marriageText = e.GetLocale().GetString("miki_placeholder_null");
                }

                embed.AddInlineField(
                    e.GetLocale().GetString("miki_module_accounts_profile_marriedto"),
                    marriageText);
            }

            Random r = new Random((int)(discordUser.Id - 3));
            Color c = new Color((float)r.NextDouble(), (float)r.NextDouble(), (float)r.NextDouble());

            embed.SetColor(c);

            List<Achievement> allAchievements = await context.Achievements
                .Where(x => x.UserId == (long)discordUser.Id)
                .ToListAsync();

            string achievements = e.GetLocale().GetString("miki_placeholder_null");
            if(allAchievements != null
                && allAchievements.Count > 0)
            {
                achievements = e.GetService<AchievementService>().PrintAchievements(allAchievements);
            }

            embed.AddInlineField(e.GetLocale().GetString("miki_generic_achievements"), achievements);
            await embed.ToEmbed().QueueAsync(e, e.GetChannel());
        }

        [Command("setbackground")]
        public async Task SetProfileBackgroundAsync(IContext e)
        {
            if(!e.GetArgumentPack().Take(out int backgroundId))
            {
                throw new ArgumentNullException("background");
            }

            long userId = e.GetAuthor().Id.ToDbLong();

            var context = e.GetService<MikiDbContext>();

            BackgroundsOwned bo = await context.BackgroundsOwned.FindAsync(userId, backgroundId);
            if(bo == null)
            {
                throw new BackgroundNotOwnedException();
            }

            ProfileVisuals v = await ProfileVisuals.GetAsync(userId, context);
            v.BackgroundId = bo.BackgroundId;
            await context.SaveChangesAsync();

            await e.SuccessEmbed("Successfully set background.")
                .QueueAsync(e, e.GetChannel());
        }

        [Command("buybackground")]
        public async Task BuyProfileBackgroundAsync(IContext e)
        {
            var backgrounds = e.GetService<BackgroundStore>();

            if(!e.GetArgumentPack().Take(out int id))
            {
                e.GetChannel().QueueMessage(e, "Enter a number after `>buybackground` to check the backgrounds! (e.g. >buybackground 1)");
            }

            if(id >= backgrounds.Backgrounds.Count || id < 0)
            {
                await e.ErrorEmbed("This background does not exist!")
                    .ToEmbed()
                    .QueueAsync(e, e.GetChannel());
                return;
            }

            Background background = backgrounds.Backgrounds[id];

            var embed = new EmbedBuilder()
                .SetTitle("Buy Background")
                .SetImage(background.ImageUrl);

            if(background.Price > 0)
            {
                embed.SetDescription($"This background for your profile will cost {background.Price.ToFormattedString()} mekos, Type `>buybackground {id} yes` to buy.");
            }
            else
            {
                embed.SetDescription("This background is not for sale.");
            }

            if(e.GetArgumentPack().Take(out string confirmation))
            {
                if(confirmation.ToLower() == "yes")
                {
                    if(background.Price > 0)
                    {
                        var context = e.GetService<MikiDbContext>();

                        User user = await User.GetAsync(context, e.GetAuthor().Id, e.GetAuthor().Username);
                        long userId = (long)e.GetAuthor().Id;

                        BackgroundsOwned bo = await context.BackgroundsOwned.FindAsync(userId, background.Id);

                        if(bo == null)
                        {
                            user.RemoveCurrency(background.Price);
                            await context.BackgroundsOwned.AddAsync(new BackgroundsOwned()
                            {
                                UserId = e.GetAuthor().Id.ToDbLong(),
                                BackgroundId = background.Id,
                            });

                            await context.SaveChangesAsync();

                            await e.SuccessEmbed("Background purchased!")
                                .QueueAsync(e, e.GetChannel());

                        }
                        else
                        {
                            throw new BackgroundOwnedException();
                        }
                    }
                    return;
                }
            }

            await embed.ToEmbed()
                .QueueAsync(e, e.GetChannel());
        }

        [Command("setbackcolor")]
        public async Task SetProfileBackColorAsync(IContext e)
        {
            var context = e.GetService<MikiDbContext>();
            User user = await DatabaseHelpers.GetUserAsync(context, e.GetAuthor());

            var x = Regex.Matches(e.GetArgumentPack().Pack.TakeAll().ToUpper(), "(#)?([A-F0-9]{6})");

            if(x.Count > 0)
            {
                ProfileVisuals visuals = await ProfileVisuals.GetAsync(e.GetAuthor().Id, context);
                var hex = (x.First().Groups as IEnumerable<Group>).Last().Value;

                visuals.BackgroundColor = hex;
                user.RemoveCurrency(250);
                await context.SaveChangesAsync();

                await e.SuccessEmbed("Your foreground color has been successfully " +
                                     $"changed to `{hex}`")
                    .QueueAsync(e, e.GetChannel());
            }
            else
            {
                await new EmbedBuilder()
                    .SetTitle("🖌 Setting a background color!")
                    .SetDescription("Changing your background color costs 250 mekos. " +
                                    "use `>setbackcolor (e.g. #00FF00)` to purchase")
                    .ToEmbed().QueueAsync(e, e.GetChannel());
            }
        }

        [Command("setfrontcolor")]
        public async Task SetProfileForeColorAsync(IContext e)
        {
            var context = e.GetService<MikiDbContext>();

            User user = await DatabaseHelpers.GetUserAsync(context, e.GetAuthor());

            var x = Regex.Matches(e.GetArgumentPack().Pack.TakeAll().ToUpper(), "(#)?([A-F0-9]{6})");

            if(x.Count > 0)
            {
                ProfileVisuals visuals = await ProfileVisuals.GetAsync(e.GetAuthor().Id, context);
                var hex = (x.First().Groups as IEnumerable<Group>).Last().Value;

                visuals.ForegroundColor = hex;
                user.RemoveCurrency(250);
                await context.SaveChangesAsync();

                await e.SuccessEmbed($"Your foreground color has been successfully changed to `{hex}`")
                    .QueueAsync(e, e.GetChannel());
            }
            else
            {
                await new EmbedBuilder()
                    .SetTitle("🖌 Setting a foreground color!")
                    .SetDescription("Changing your foreground(text) color costs 250 " +
                                    "mekos. use `>setfrontcolor (e.g. #00FF00)` to purchase")
                    .ToEmbed().QueueAsync(e, e.GetChannel());
            }
        }

        [Command("backgroundsowned")]
        public async Task BackgroundsOwnedAsync(IContext e)
        {
            var context = e.GetService<MikiDbContext>();

            List<BackgroundsOwned> backgroundsOwned = await context.BackgroundsOwned.Where(x => x.UserId == e.GetAuthor().Id.ToDbLong())
                    .ToListAsync();

            await new EmbedBuilder()
                .SetTitle($"{e.GetAuthor().Username}'s backgrounds")
                .SetDescription(string.Join(",", backgroundsOwned.Select(x => $"`{x.BackgroundId}`")))
                .ToEmbed()
                .QueueAsync(e, e.GetChannel());
        }

        [Command("rep")]
        public async Task GiveReputationAsync(IContext e)
        {
            var context = e.GetService<MikiDbContext>();

            User giver = await context.Users.FindAsync(e.GetAuthor().Id.ToDbLong());

            var cache = e.GetService<ICacheClient>();

            var repObject = await cache.GetAsync<ReputationObject>($"user:{giver.Id}:rep");

            if(repObject == null)
            {
                repObject = new ReputationObject()
                {
                    LastReputationGiven = DateTime.Now,
                    ReputationPointsLeft = 3
                };

                await cache.UpsertAsync(
                    $"user:{giver.Id}:rep",
                    repObject,
                    DateTime.UtcNow.AddDays(1).Date - DateTime.UtcNow
                );
            }

            if(!e.GetArgumentPack().CanTake)
            {
                TimeSpan pointReset = (DateTime.Now.AddDays(1).Date - DateTime.Now);

                await new EmbedBuilder()
                {
                    Title = e.GetLocale().GetString("miki_module_accounts_rep_header"),
                    Description = e.GetLocale().GetString("miki_module_accounts_rep_description")
                }.AddInlineField(
                        e.GetLocale().GetString("miki_module_accounts_rep_total_received"),
                        giver.Reputation.ToString("N0"))
                    .AddInlineField(
                        e.GetLocale().GetString("miki_module_accounts_rep_reset"),
                        pointReset.ToTimeString(e.GetLocale()))
                    .AddInlineField(
                        e.GetLocale().GetString("miki_module_accounts_rep_remaining"),
                        repObject.ReputationPointsLeft.ToString())
                    .ToEmbed()
                    .QueueAsync(e, e.GetChannel());
                return;
            }
            else
            {
                Dictionary<IDiscordUser, short> usersMentioned = new Dictionary<IDiscordUser, short>();

                EmbedBuilder embed = new EmbedBuilder();

                int totalAmountGiven = 0;
                bool mentionedSelf = false;

                while(e.GetArgumentPack().CanTake && totalAmountGiven <= repObject.ReputationPointsLeft)
                {
                    short amount = 1;

                    e.GetArgumentPack().Take(out string userName);

                    var u = await DiscordExtensions.GetUserAsync(userName, e.GetGuild());

                    if(u == null)
                    {
                        throw new UserNullException();
                    }

                    if(e.GetArgumentPack().Take(out int value))
                    {
                        amount = (short)value;
                    }
                    else if(e.GetArgumentPack().Peek(out string arg))
                    {
                        if(Utils.IsAll(arg))
                        {
                            amount = (short)(repObject.ReputationPointsLeft - ((short)usersMentioned.Sum(x => x.Value)));
                            e.GetArgumentPack().Skip();
                        }
                    }

                    if(u.Id == e.GetAuthor().Id)
                    {
                        mentionedSelf = true;
                        continue;
                    }

                    totalAmountGiven += amount;

                    if(usersMentioned.Keys.Any(x => x.Id == u.Id))
                    {
                        usersMentioned[usersMentioned.Keys.First(x => x.Id == u.Id)] += amount;
                    }
                    else
                    {
                        usersMentioned.Add(u, amount);
                    }
                }

                if(mentionedSelf)
                {
                    embed.Footer = new EmbedFooter()
                    {
                        Text = e.GetLocale().GetString("warning_mention_self"),
                    };
                }

                if(usersMentioned.Count == 0)
                {
                    return;
                }
                else
                {
                    if(totalAmountGiven <= 0)
                    {
                        await e.ErrorEmbedResource("miki_module_accounts_rep_error_zero")
                            .ToEmbed().QueueAsync(e, e.GetChannel());
                        return;
                    }

                    if(usersMentioned.Sum(x => x.Value) > repObject.ReputationPointsLeft)
                    {
                        await e.ErrorEmbedResource(
                                "error_rep_limit", 
                                usersMentioned.Count, 
                                usersMentioned.Sum(x => x.Value), repObject.ReputationPointsLeft)
                            .ToEmbed().QueueAsync(e, e.GetChannel());
                        return;
                    }
                }

                embed.Title = (e.GetLocale().GetString("miki_module_accounts_rep_header"));
                embed.Description = (e.GetLocale().GetString("rep_success"));

                foreach(var u in usersMentioned)
                {
                    User receiver = await DatabaseHelpers.GetUserAsync(context, u.Key);

                    receiver.Reputation += u.Value;

                    embed.AddInlineField(
                        receiver.Name,
                        $"{(receiver.Reputation - u.Value):N0} => {receiver.Reputation:N0} (+{u.Value})"
                    );
                }

                repObject.ReputationPointsLeft -= (short)usersMentioned.Sum(x => x.Value);

                await cache.UpsertAsync(
                    $"user:{giver.Id}:rep",
                    repObject,
                    DateTime.UtcNow.AddDays(1).Date - DateTime.UtcNow
                );

                await embed.AddInlineField(e.GetLocale().GetString("miki_module_accounts_rep_points_left"), repObject.ReputationPointsLeft.ToString())
                    .ToEmbed().QueueAsync(e, e.GetChannel());

                await context.SaveChangesAsync();
            }
        }

        [Command("syncname")]
        public async Task SyncNameAsync(IContext e)
        {
            var context = e.GetService<MikiDbContext>();

            User user = await context.Users.FindAsync(e.GetAuthor().Id.ToDbLong());

            if(user == null)
            {
                throw new UserNullException();
            }

            user.Name = e.GetAuthor().Username;
            await context.SaveChangesAsync();

            await new EmbedBuilder()
            {
                Title = "👌 OKAY",
                Description = e.GetLocale().GetString("sync_success", "name")
            }.ToEmbed().QueueAsync(e, e.GetChannel());
        }

        [Command("mekos", "bal", "meko")]
        public async Task ShowMekosAsync(IContext e)
        {
            IDiscordGuildUser member;

            if(e.GetArgumentPack().Take(out string value))
            {
                member = await DiscordExtensions.GetUserAsync(value, e.GetGuild());
            }
            else
            {
                member = await e.GetGuild().GetMemberAsync(e.GetAuthor().Id);
            }

            var context = e.GetService<MikiDbContext>();

            User user = await User.GetAsync(context, member.Id.ToDbLong(), member.Username);

            await new EmbedBuilder()
            {
                Title = "🔸 Mekos",
                Description = e.GetLocale().GetString("miki_user_mekos", user.Name, user.Currency.ToString("N0")),
                Color = new Color(1f, 0.5f, 0.7f)
            }.ToEmbed().QueueAsync(e, e.GetChannel());
            await context.SaveChangesAsync();
        }

        [Command("give")]
        public async Task GiveMekosAsync(IContext e)
        {
            if (e.GetArgumentPack().Take(out string userName))
            {
                var user = await DiscordExtensions.GetUserAsync(userName, e.GetGuild());

                if (user == null)
                {
                    await e.ErrorEmbedResource("give_error_no_mention")
                        .ToEmbed().QueueAsync(e, e.GetChannel());
                    return;
                }

                if (!e.GetArgumentPack().Take(out int amount))
                {
                    await e.ErrorEmbedResource("give_error_amount_unparsable")
                        .ToEmbed().QueueAsync(e, e.GetChannel());
                    return;
                }

                var context = e.GetService<MikiDbContext>();

                User sender = await DatabaseHelpers.GetUserAsync(context, e.GetAuthor());
                User receiver = await DatabaseHelpers.GetUserAsync(context, user);

                if(await receiver.IsBannedAsync(context))
                {
                    throw new UserNullException();
                }

                sender.RemoveCurrency(amount);
                receiver.AddCurrency(amount);

                await new EmbedBuilder()
                {
                    Title = "🔸 transaction",
                    Description = e.GetLocale().GetString(
                        "give_description",
                        sender.Name,
                        receiver.Name,
                        amount.ToFormattedString()),
                    Color = new Color(255, 140, 0),
                }.ToEmbed().QueueAsync(e, e.GetChannel());
                await context.SaveChangesAsync();
            }
        }

        [Command("daily")]
        public async Task GetDailyAsync(IContext e)
        {
            var context = e.GetService<MikiDbContext>();

            User u = await DatabaseHelpers.GetUserAsync(context, e.GetAuthor());

            if(u == null)
            {
                await e.ErrorEmbed(e.GetLocale().GetString("user_error_no_account"))
                    .ToEmbed().QueueAsync(e, e.GetChannel());
                return;
            }

            int dailyAmount = 100;
            int dailyStreakAmount = 20;

            if(await u.IsDonatorAsync(context))
            {
                dailyAmount *= 2;
                dailyStreakAmount *= 2;
            }

            if(u.LastDailyTime.AddHours(23) >= DateTime.UtcNow)
            {
                var time = (u.LastDailyTime.AddHours(23) - DateTime.UtcNow).ToTimeString(e.GetLocale());

                var builder = e.ErrorEmbed($"You already claimed your daily today! Please wait another `{time}` before using it again.");

                switch(MikiRandom.Next(2))
                {
                    case 0:
                    {
                        builder.AddInlineField("Appreciate Miki?", "Vote for us every day on [DiscordBots](https://discordbots.org/bot/160105994217586689/vote) to get an additional bonus!");
                    }
                    break;
                    case 1:
                    {
                        builder.AddInlineField("Appreciate Miki?", "Donate to us on [Patreon](https://patreon.com/mikibot) for more mekos!");
                    }
                    break;
                }
                await builder.ToEmbed()
                    .QueueAsync(e, e.GetChannel());
                return;
            }

            int streak = 0;
            string redisKey = $"user:{e.GetAuthor().Id}:daily";

            var cache = e.GetService<ICacheClient>();

            if(await cache.ExistsAsync(redisKey))
            {
                streak = await cache.GetAsync<int>(redisKey);
                streak++;
            }

            int amount = dailyAmount + (dailyStreakAmount * Math.Min(100, streak));

            u.AddCurrency(amount);
            u.LastDailyTime = DateTime.UtcNow;

            var embed = new EmbedBuilder()
                .SetTitle("💰 Daily")
                .SetDescription(e.GetLocale().GetString(
                    "daily_received", 
                    $"**{amount:N0}**", 
                    $"`{u.Currency.ToFormattedString()}`"))
                .SetColor(253, 216, 136);

            if(streak > 0)
            {
                embed.AddInlineField("Streak!", $"You're on a {streak.ToFormattedString()} day daily streak!");
            }

            await embed.ToEmbed().QueueAsync(e, e.GetChannel());

            await cache.UpsertAsync(redisKey, streak, new TimeSpan(48, 0, 0));
            await context.SaveChangesAsync();
        }
    }
}