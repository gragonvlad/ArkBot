﻿using ArkBot.Commands;
using ArkBot.Data;
using ArkBot.Database;
using ArkBot.OpenID;
using ArkBot.Extensions;
using Discord;
using Discord.Commands;
using Google.Apis.Urlshortener.v1;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using ArkBot.Database.Model;
using ArkBot.Helpers;
using Autofac;
using ArkBot.Vote;
using log4net;
using System.Data.Entity.Core.Objects;
using ArkBot.ViewModel;

namespace ArkBot
{
    public class ArkDiscordBot : IDisposable
    {
        private DiscordClient _discord;
        private IArkContext _context;
        private IConfig _config;
        private IConstants _constants;
        private IBarebonesSteamOpenId _openId;
        private EfDatabaseContextFactory _databaseContextFactory;
        private Timer _timer;

        private TimeSpan? _prevNextUpdate;
        private DateTime _prevLastUpdate;
        private ConcurrentDictionary<TimedTask, bool> _timedTasks;
        private DateTime _prevTimedBansUpdate;
        private ILifetimeScope _scope;

        private bool _wasRestarted;
        private List<ulong> _wasRestartedServersNotified = new List<ulong>();

        public ArkDiscordBot(IConfig config, IArkContext context, IConstants constants, IBarebonesSteamOpenId openId, EfDatabaseContextFactory databaseContextFactory, IEnumerable<ICommand> commands, ILifetimeScope scope)
        {
            _config = config;
            _context = context;
            _constants = constants;
            _databaseContextFactory = databaseContextFactory;
            _openId = openId;
            _openId.SteamOpenIdCallback += _openId_SteamOpenIdCallback;
            _scope = scope;

            _context.Updated += _context_Updated;

            _discord = new DiscordClient(x =>
           {
               x.LogLevel = _config.Debug ? LogSeverity.Info : LogSeverity.Warning;
               x.LogHandler += Log;
               x.AppName = _config.BotName;
               x.AppUrl = !string.IsNullOrWhiteSpace(_config.BotUrl) ? _config.BotUrl : null;
           });

            _discord.UsingCommands(x =>
            {
                x.PrefixChar = '!';
                x.AllowMentionPrefix = true;
            });

            _discord.ServerAvailable += _discord_ServerAvailable;

            var cservice = _discord.GetService<CommandService>();
            cservice.CommandExecuted += Commands_CommandExecuted;
            cservice.CommandErrored += Commands_CommandErrored;
            foreach(var command in commands)
            {
                if (command.DebugOnly && !_config.Debug) continue;

                var cbuilder = cservice.CreateCommand(command.Name);
                if (command.Aliases != null && command.Aliases.Length > 0) cbuilder.Alias(command.Aliases);
                var rrc = command as IRoleRestrictedCommand;
                if (rrc != null && rrc.ForRoles?.Length > 0)
                {
                    cbuilder.AddCheck((a, b, c) => 
                    c.Client.Servers.Any(x => 
                    x.Roles.Any(y => y != null && rrc.ForRoles.Contains(y.Name, StringComparer.OrdinalIgnoreCase) == true && y.Members.Any(z => z.Id == b.Id))), null);
                }

                cbuilder.AddCheck((a, b, c) =>
                {
                    return c.IsPrivate || !(_config.EnabledChannels?.Length > 0) || (c?.Name != null && _config.EnabledChannels.Contains(c.Name, StringComparer.OrdinalIgnoreCase));
                });

                command.Init(_discord);
                command.Register(cbuilder);
                cbuilder.Do(command.Run);
            }

            _timedTasks = new ConcurrentDictionary<TimedTask, bool>();
            _timer = new Timer(_timer_Callback, null, TimeSpan.Zero, TimeSpan.FromSeconds(1));

            {
                _context.VoteInitiated += _context_VoteInitiated;
                _context.VoteResultForced += _context_VoteResultForced;
            }

            var args = Environment.GetCommandLineArgs();
            if (args != null && args.Contains("/restart", StringComparer.OrdinalIgnoreCase))
            {
                _wasRestarted = true;
            }
        }

        private async void _context_VoteResultForced(object sender, VoteResultForcedEventArgs args)
        {
            if (args == null || args.Item == null) return;

            var tasks = _timedTasks.Keys.Where(x => x.Tag is string && ((string)x.Tag).Equals("vote_" + args.Item.Id, StringComparison.OrdinalIgnoreCase)).ToArray();
            foreach (var task in tasks)
            {
                bool tmp;
                _timedTasks.TryRemove(task, out tmp);
            }

            using (var db = _databaseContextFactory.Create())
            {
                var vote = db.Votes.FirstOrDefault(x => x.Id == args.Item.Id);

                await VoteFinished(db, vote, forcedResult: args.Result);
            }
        }

        private IVoteHandler GetVoteHandler(Database.Model.Vote vote)
        {
            IVoteHandler handler = null;
            var type = ObjectContext.GetObjectType(vote.GetType());
            try
            {
                handler = _scope.Resolve(typeof(IVoteHandler<>).MakeGenericType(type), new TypedParameter(typeof(Database.Model.Vote), vote)) as IVoteHandler;
            }
            catch (Exception ex)
            {
                Logging.LogException($"Failed to resolve IVoteHandler for type '{type.Name}'", ex, GetType(), LogLevel.ERROR, ExceptionLevel.Unhandled);
                return null;
            }

            if (handler == null)
            {
                Logging.Log($"Failed to resolve IVoteHandler for type '{type.Name}'", GetType(), LogLevel.ERROR);
                return null;
            }

            return handler;
        }

        private void _context_VoteInitiated(object sender, VoteInitiatedEventArgs args)
        {
            if (args == null || args.Item == null) return;

            var handler = GetVoteHandler(args.Item);
            if (handler == null) return;

            //reminder, one minute before expiry
            var reminderAt = args.Item.Finished.AddMinutes(-1);
            if (DateTime.Now < reminderAt)
            {
                _timedTasks.TryAdd(new TimedTask
                {
                    When = reminderAt,
                    Tag = "vote_" + args.Item.Id,
                    Callback = new Func<Task>(async () =>
                    {
                        var result = handler.VoteIsAboutToExpire();
                        if (result == null) return;

                        if (result.MessageRcon != null) await CommandHelper.SendRconCommand(_config, $"serverchat {result.MessageRcon.ReplaceRconSpecialChars()}");
                        if (result.MessageAnnouncement != null && !string.IsNullOrWhiteSpace(_config.AnnouncementChannel))
                        {
                            var channels = _discord.Servers.Select(x => x.TextChannels.FirstOrDefault(y => _config.AnnouncementChannel.Equals(y.Name, StringComparison.OrdinalIgnoreCase))).Where(x => x != null);
                            foreach (var channel in channels)
                            {
                                await channel.SendMessage(result.MessageAnnouncement);
                            }
                        }
                    })
                }, true);
            }

            //on elapsed
            _timedTasks.TryAdd(new TimedTask
            {
                When = args.Item.Finished,
                Tag = "vote_" + args.Item.Id,
                Callback = new Func<Task>(async () =>
                {
                    using (var db = _databaseContextFactory.Create())
                    {
                        var vote = db.Votes.FirstOrDefault(x => x.Id == args.Item.Id);

                        await VoteFinished(db, vote);
                    }
                })
            }, true);
        }

        private async Task VoteFinished(IEfDatabaseContext db, Database.Model.Vote vote, bool noAnnouncement = false, VoteResult? forcedResult = null)
        {
            var handler = GetVoteHandler(vote);
            if (handler == null) return;

            var votesFor = vote.Votes.Count(x => x.VotedFor);
            var votesAgainst = vote.Votes.Count(x => !x.VotedFor);
            vote.Result = forcedResult ?? (vote.Votes.Count >= (_config.Debug ? 1 : 3) && votesFor > votesAgainst ? VoteResult.Passed : VoteResult.Failed);

            Vote.VoteStateChangeResult result = null;
            try
            {
                result = await handler.VoteFinished(_config, _constants, db);
                try
                {
                    if (!noAnnouncement && result != null)
                    {
                        if (result.MessageRcon != null) await CommandHelper.SendRconCommand(_config, $"serverchat {result.MessageRcon.ReplaceRconSpecialChars()}");
                        if (result.MessageAnnouncement != null && !string.IsNullOrWhiteSpace(_config.AnnouncementChannel))
                        {
                            var channels = _discord.Servers.Select(x => x.TextChannels.FirstOrDefault(y => _config.AnnouncementChannel.Equals(y.Name, StringComparison.OrdinalIgnoreCase))).Where(x => x != null);
                            foreach (var channel in channels)
                            {
                                await channel.SendMessage(result.MessageAnnouncement);
                            }
                        }
                    }
                }
                catch { /* ignore all exceptions */ }

                if (result != null && result.React != null)
                {
                    if (result.ReactDelayInMinutes <= 0) await result.React();
                    else
                    {
                        await CommandHelper.SendRconCommand(_config, $"serverchat Countdown started: {result.ReactDelayFor} in {result.ReactDelayInMinutes} minutes...");
                        if (!string.IsNullOrWhiteSpace(_config.AnnouncementChannel))
                        {
                            var channels = _discord.Servers.Select(x => x.TextChannels.FirstOrDefault(y => _config.AnnouncementChannel.Equals(y.Name, StringComparison.OrdinalIgnoreCase))).Where(x => x != null);
                            foreach (var channel in channels)
                            {
                                await channel.SendMessage($"**Countdown started: {result.ReactDelayFor} in {result.ReactDelayInMinutes} minutes...**");
                            }
                        }

                        foreach (var min in Enumerable.Range(1, result.ReactDelayInMinutes))
                        {
                            _timedTasks.TryAdd(new TimedTask
                            {
                                When = DateTime.Now.AddMinutes(min),
                                Callback = new Func<Task>(async () =>
                                {
                                    var countdown = result.ReactDelayInMinutes - min;
                                    await CommandHelper.SendRconCommand(_config, $"serverchat {result.ReactDelayFor} in {countdown} minutes...");
                                    if (!string.IsNullOrWhiteSpace(_config.AnnouncementChannel))
                                    {
                                        var channels = _discord.Servers.Select(x => x.TextChannels.FirstOrDefault(y => _config.AnnouncementChannel.Equals(y.Name, StringComparison.OrdinalIgnoreCase))).Where(x => x != null);
                                        foreach (var channel in channels)
                                        {
                                            await channel.SendMessage($"**{result.ReactDelayFor} in {countdown} minutes...**");
                                        }
                                    }
                                    if (countdown <= 0) await result.React();
                                })
                            }, true);
                        }
                    }
                }
            }
            catch(Exception ex)
            {
                //todo: better exception handling structure
                Logging.LogException(ex.Message, ex, GetType(), LogLevel.ERROR, ExceptionLevel.Unhandled);
            }
            db.SaveChanges();
            
        }

        /// <summary>
        /// Main proceedure
        /// </summary>
        private async void _timer_Callback(object state)
        {
            try
            {
                _timer.Change(Timeout.Infinite, Timeout.Infinite);

                var tasks = _timedTasks.Keys.Where(x => x.When <= DateTime.Now).ToArray();
                foreach(var task in tasks)
                {
                    bool tmp;
                    _timedTasks.TryRemove(task, out tmp);

                    await task.Callback();
                }

                if (_config.InfoTopicChannel != null)
                {
                    var lastUpdate = _context.LastUpdate;
                    var nextUpdate = _context.ApproxTimeUntilNextUpdate;
                    if ((lastUpdate != _prevLastUpdate || nextUpdate != _prevNextUpdate))
                    {
                        _prevLastUpdate = lastUpdate;
                        _prevNextUpdate = nextUpdate;

                        var nextUpdateTmp = nextUpdate?.ToStringCustom();
                        var nextUpdateString = (nextUpdate.HasValue ? (!string.IsNullOrWhiteSpace(nextUpdateTmp) ? $", Next Update in ~{nextUpdateTmp}" : ", waiting for new update ...") : "");
                        var lastUpdateString = lastUpdate.ToStringWithRelativeDay();
                        var newtopic = $"Updated {lastUpdateString}{nextUpdateString} | Type !help to get started";

                        try
                        {
                            var channels = _discord.Servers.Select(x => x.TextChannels.FirstOrDefault(y => _config.InfoTopicChannel.Equals(y.Name, StringComparison.OrdinalIgnoreCase))).Where(x => x != null);
                            foreach (var channel in channels)
                            {
                                await channel.Edit(topic: newtopic);
                            }
                        }
                        catch(Exception ex)
                        {
                            Logging.LogException("Error when attempting to change bot info channel topic", ex, GetType(), LogLevel.ERROR, ExceptionLevel.Ignored);
                        }
                    }
                }

                if (DateTime.Now- _prevTimedBansUpdate > TimeSpan.FromMinutes(5))
                {
                    _prevTimedBansUpdate = DateTime.Now;

                    using (var db = _databaseContextFactory.Create())
                    {
                        var elapsedBans = db.Votes.OfType<BanVote>().Where(x => x.Result == VoteResult.Passed && x.BannedUntil.HasValue && x.BannedUntil <= DateTime.Now).ToArray();
                        foreach(var ban in elapsedBans)
                        {
                                if (await CommandHelper.SendRconCommand(_config, $"unbanplayer {ban.SteamId}") != null) ban.BannedUntil = null;
                        }

                        db.SaveChanges();
                    }
                }
            }
            catch (Exception ex)
            {
                Logging.LogException("Unhandled exception in bot timer method", ex, GetType(), LogLevel.ERROR, ExceptionLevel.Unhandled);
            }
            finally
            {
                _timer.Change(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
            }
        }

        private async void _discord_ServerAvailable(object sender, ServerEventArgs e)
        {
            if (_wasRestarted && e?.Server != null && !string.IsNullOrWhiteSpace(_config.AnnouncementChannel) && !_wasRestartedServersNotified.Contains(e.Server.Id))
            {
                try
                {
                    _wasRestartedServersNotified.Add(e.Server.Id);
                    var channel = e.Server.TextChannels.FirstOrDefault(y => _config.AnnouncementChannel.Equals(y.Name, StringComparison.OrdinalIgnoreCase));
                    if (channel != null) await channel.SendMessage("**I have automatically restarted due to previous unexpected shutdown!**");
                }
                catch (Exception ex) { /*ignore exceptions */ }
            }

            await UpdateNicknamesAndRoles(e.Server); 
        }

        /// <summary>
        /// All context data have been updated (occurs on start and when a savefile change have been handled)
        /// </summary>
        private async void _context_Updated(object sender, EventArgs e)
        {
            //on the first update triggered on start, servers are not yet connected so this code will not run.
            await UpdateNicknamesAndRoles();
        }

        private async Task UpdateNicknamesAndRoles(Server _server = null)
        {
            try
            {
                //change nicknames, add/remove from ark-role
                Database.Model.User[] linkedusers = null;
                using (var db = _databaseContextFactory.Create())
                {
                    linkedusers = db.Users.Where(x => !x.Unlinked).ToArray();
                }

                foreach (var server in _discord.Servers)
                {
                    if (_server != null && server.Id != _server.Id) continue;

                    var role = server.FindRoles(_config.MemberRoleName, true).FirstOrDefault();
                    if (role == null) continue;

                    foreach (var user in server.Users)
                    {
                        try
                        {
                            var dbuser = linkedusers.FirstOrDefault(x => (ulong)x.DiscordId == user.Id);
                            if (dbuser == null)
                            {
                                if (user.HasRole(role))
                                {
                                    Logging.Log($@"Removing role ({role.Name}) from user ({user.Name}#{user.Discriminator})", GetType(), LogLevel.DEBUG);
                                    await user.RemoveRoles(role);
                                }
                                continue;
                            }

                            if (!user.HasRole(role))
                            {
                                Logging.Log($@"Adding role ({role.Name}) from user ({user.Name}#{user.Discriminator})", GetType(), LogLevel.DEBUG);
                                await user.AddRoles(role);
                            }

                            var player = _context.Players?.FirstOrDefault(x => { long steamId = 0; return long.TryParse(x.SteamId, out steamId) ? steamId == dbuser.SteamId : false; });
                            var playerName = player?.Name?.Length > 32 ? player?.Name?.Substring(0, 32) : player?.Name;
                            if (!string.IsNullOrWhiteSpace(playerName) && (user.Nickname == null || !playerName.Equals(user.Nickname, StringComparison.Ordinal)))
                            {
                                //must be less or equal to 32 characters
                                Logging.Log($@"Changing nickname (from: ""{user.Nickname ?? "null"}"", to: ""{playerName}"") for user ({user.Name}#{user.Discriminator})", GetType(), LogLevel.DEBUG);
                                await user.Edit(nickname: playerName);
                            }
                        }
                        catch (Discord.Net.HttpException ex)
                        {
                            //could be due to the order of roles on the server. bot role with "manage roles"/"change nickname" permission must be higher up than the role it is trying to set
                            Logging.LogException("HttpException while trying to update nicknames/roles (could be due to permissions)", ex, GetType(), LogLevel.DEBUG, ExceptionLevel.Ignored);
                        }
                    }
                }
            }
            catch(WebException ex)
            {
                Logging.LogException("Exception while trying to update nicknames/roles", ex, GetType(), LogLevel.DEBUG, ExceptionLevel.Ignored);
            }
        }

        private async void _openId_SteamOpenIdCallback(object sender, SteamOpenIdCallbackEventArgs e)
        {
            if (e.Successful)
            {
                var player = new
                {
                    RealName = (string)null,
                    PersonaName = (string)null
                };
                try
                {
                    using (var wc = new WebClient())
                    {
                        var data = await wc.DownloadStringTaskAsync($@"http://api.steampowered.com/ISteamUser/GetPlayerSummaries/v0002/?key={_config.SteamApiKey}&steamids={e.SteamId}");
                        var response = JsonConvert.DeserializeAnonymousType(data, new { response = new { players = new[] { player } } });
                        player = response?.response?.players?.FirstOrDefault();
                    }
                }
                catch { /* ignore exceptions */ }

                //QueryMaster.Steam.GetPlayerSummariesResponsePlayer player = null;
                //await Task.Factory.StartNew(() =>
                //{
                //    try
                //    {
                //        //this results in an exception (but it is easy enough to query by ourselves)
                //        var query = new QueryMaster.Steam.SteamQuery(_config.SteamApiKey);
                //        var result = query?.ISteamUser.GetPlayerSummaries(new[] { e.SteamId });
                //        if (result == null || !result.IsSuccess) return;

                //        player = result.ParsedResponse.Players.FirstOrDefault();
                //    }
                //    catch { /* ignore exceptions */}
                //});

                //set ark role on users when they link
                foreach(var server in _discord.Servers)
                {
                    var user = server.GetUser(e.DiscordUserId);
                    var role = server.FindRoles(_config.MemberRoleName, true).FirstOrDefault();
                    if (user == null || role == null) continue;

                    try
                    {
                        if (!user.HasRole(role)) await user.AddRoles(role);

                        var p = _context.Players?.FirstOrDefault(x => { ulong steamId = 0; return ulong.TryParse(x.SteamId, out steamId) ? steamId == e.SteamId : false; });
                        if (p != null && !string.IsNullOrWhiteSpace(p.Name))
                        {

                            //must be less or equal to 32 characters
                            await user.Edit(nickname: p.Name.Length > 32 ? p.Name.Substring(0, 32) : p.Name);

                        }
                    }
                    catch (Discord.Net.HttpException)
                    {
                        //could be due to the order of roles on the server. bot role with "manage roles"/"change nickname" permission must be higher up than the role it is trying to set
                    }
                }

                using (var context = _databaseContextFactory.Create())
                {
                    var user = context.Users.FirstOrDefault(x => x.DiscordId == (long)e.DiscordUserId);
                    if (user != null)
                    {
                        user.RealName = player?.RealName;
                        user.SteamDisplayName = player?.PersonaName;
                        user.SteamId = (long)e.SteamId;
                        user.Unlinked = false;
                    }
                    else
                    {
                        user = new Database.Model.User { DiscordId = (long)e.DiscordUserId, SteamId = (long)e.SteamId, RealName = player?.RealName, SteamDisplayName = player?.PersonaName };
                        context.Users.Add(user);
                    }

                    foreach(var associatePlayed in context.Played.Where(x => x.SteamId == (long)e.SteamId))
                    {
                        associatePlayed.SteamId = null;
                        user.Played.Add(associatePlayed);
                    }

                    context.SaveChanges();
                }
                var ch = await _discord.CreatePrivateChannel(e.DiscordUserId);
                await ch?.SendMessage($"Your Discord user is now linked with your Steam account! :)");
            }
            else
            {
                var ch = await _discord.CreatePrivateChannel(e.DiscordUserId);
                await ch?.SendMessage($"Something went wrong during the linking process. Please try again later!");
            }
        }

        private void Commands_CommandErrored(object sender, CommandErrorEventArgs e)
        {
            if (e == null || e.Command == null || e.Command.IsHidden || e.ErrorType == CommandErrorType.BadPermissions) return;
            var sb = new StringBuilder();
            var message = $@"""!{e.Command.Text}{(e.Args.Length > 0 ? " " : "")}{string.Join(" ", e.Args)}"" command error...";
            sb.AppendLine(message);
            if(e.Exception != null) sb.AppendLine($"Exception: {e.Exception.ToString()}");
            sb.AppendLine();
            _context.Progress.Report(sb.ToString());

            //if there is an exception log all information pertaining to it so that we can possibly fix it in the future
            if (e.Exception != null) Logging.LogException(message, e.Exception, GetType(), LogLevel.ERROR, ExceptionLevel.Unhandled);
        }

        private void Commands_CommandExecuted(object sender, CommandEventArgs e)
        {
            if (e == null || e.Command == null || e.Command.IsHidden) return;

            var sb = new StringBuilder();
            sb.AppendLine($@"""!{e.Command.Text}{(e.Args.Length > 0 ? " " : "")}{string.Join(" ", e.Args)}"" command successful!");
            _context.Progress.Report(sb.ToString());
        }

        public async Task Initialize(CancellationToken token, bool skipExtract = false, ArkSpeciesAliases aliases = null)
        {
            await _context.Initialize(token, skipExtract, aliases);

            //handle undecided votes (may happen due to previous bot shutdown before vote finished)
            using (var db = _databaseContextFactory.Create())
            {
                var votes = db.Votes.Where(x => x.Result == VoteResult.Undecided);
                foreach (var vote in votes)
                {
                    if (DateTime.Now >= vote.Finished)
                    {
                        await VoteFinished(db, vote, true);
                    }
                    else
                    {
                        _context_VoteInitiated(null, new VoteInitiatedEventArgs { Item = vote });
                    }
                }
            }
        }

        public async Task Start(ArkSpeciesAliases aliases = null)
        {
            await _discord.Connect(_config.BotToken, TokenType.Bot);
        }

        public async Task Stop()
        {
            await _discord.Disconnect();
        }

        private void Log(object sender, LogMessageEventArgs e)
        {
            Workspace.Instance.Console.AddLog(e.Message);
        }

        #region IDisposable Support
        private bool disposedValue = false; // To detect redundant calls

        protected virtual void Dispose(bool disposing)
        {
            if (!disposedValue)
            {
                if (disposing)
                {
                    // TODO: dispose managed state (managed objects).
                    _discord?.Dispose();
                    _discord = null;

                    _openId.SteamOpenIdCallback -= _openId_SteamOpenIdCallback;
                }

                // TODO: free unmanaged resources (unmanaged objects) and override a finalizer below.
                // TODO: set large fields to null.

                disposedValue = true;
            }
        }

        // TODO: override a finalizer only if Dispose(bool disposing) above has code to free unmanaged resources.
        // ~ArkBot() {
        //   // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
        //   Dispose(false);
        // }

        // This code added to correctly implement the disposable pattern.
        public void Dispose()
        {
            // Do not change this code. Put cleanup code in Dispose(bool disposing) above.
            Dispose(true);
            // TODO: uncomment the following line if the finalizer is overridden above.
            // GC.SuppressFinalize(this);
        }
        #endregion
    }
}
