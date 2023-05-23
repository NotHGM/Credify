﻿using Credify.ChatGames;
using Microsoft.Extensions.DependencyInjection;
using SharedLibraryCore;
using SharedLibraryCore.Events.Game;
using SharedLibraryCore.Events.Management;
using SharedLibraryCore.Events.Server;
using SharedLibraryCore.Interfaces;
using SharedLibraryCore.Interfaces.Events;
using Utilities = SharedLibraryCore.Utilities;

namespace Credify;

// Achievements -> get kill with X
//     MOD, Description, Amount, Payout
//     There would need to be stored current progress for each player
// Countdown style guess
//     30s. Payout is based on response time and word complexity.
//     Partially done - Need to do core logic.

public class Plugin : IPluginV2
{
    private readonly PersistenceManager _persistenceManager;
    private readonly BetManager _betManager;
    private readonly CredifyConfiguration _config;
    private readonly LotteryManager _lotteryManager;
    private readonly ChatGameManager _chatGameManager;
    private readonly ChatUtils _chatUtils;
    public const string Key = "Credits_Amount";
    public const string TopKey = "Credits_TopList";
    public const string StatisticsKey = "Credits_Statistics";
    public const string LotteryKey = "Credits_Lottery";
    public const string NextLotteryKey = "Credits_NextLottery";
    public const string ShopKey = "Credits_Shop";
    public const string BankCreditsKey = "Credits_Bank";
    public const string LastLottoWinner = "Credits_LastLottoWinner";
    public const string RecentBoughtItems = "Credits_RecentBoughtItems";

    public const string PluginName = "Credify";
    public string Name => PluginName;
    public string Version => "2023-05-23";
    public string Author => "Amos";

    public Plugin(PersistenceManager persistenceManager, BetManager betManager, CredifyConfiguration config,
        LotteryManager lotteryManager, ChatGameManager chatGameManager, ChatUtils chatUtils)
    {
        _config = config;
        _lotteryManager = lotteryManager;
        _chatGameManager = chatGameManager;
        _chatUtils = chatUtils;
        _persistenceManager = persistenceManager;
        _betManager = betManager;
        if (!config.IsEnabled) return;

        IGameEventSubscriptions.ClientKilled += OnClientKilled;
        IGameEventSubscriptions.MatchEnded += OnMatchEnded;
        IGameEventSubscriptions.ClientJoinedTeam += OnClientJoinedTeam;
        IGameEventSubscriptions.ClientMessaged += OnClientMessaged;
        IGameServerEventSubscriptions.ClientDataUpdated += OnClientDataUpdated;
        IManagementEventSubscriptions.ClientStateAuthorized += OnClientStateAuthorized;
        IManagementEventSubscriptions.ClientStateDisposed += OnClientStateDisposed;
        IManagementEventSubscriptions.Load += OnLoad;
    }

    public static void RegisterDependencies(IServiceCollection serviceCollection)
    {
        serviceCollection.AddSingleton<PersistenceManager>();
        serviceCollection.AddSingleton<BetManager>();
        serviceCollection.AddSingleton<LotteryManager>();
        serviceCollection.AddSingleton<ChatGameManager>();
        serviceCollection.AddSingleton<ChatUtils>();
        serviceCollection.AddConfiguration("CredifyConfiguration", new CredifyConfiguration());
    }

    #region Events

    private async Task OnClientMessaged(ClientMessageEvent messageEvent, CancellationToken token) =>
        await _chatGameManager.HandleChatEvent(messageEvent.Client, messageEvent.Message);

    private async Task OnMatchEnded(MatchEndEvent matchEnd, CancellationToken token) =>
        await _betManager.OnMapEndAsync(matchEnd.Owner);

    private async Task OnClientJoinedTeam(ClientJoinTeamEvent clientEvent, CancellationToken token) =>
        await _betManager.OnJoinTeamAsync(clientEvent.Client);

    private async Task OnClientStateAuthorized(ClientStateAuthorizeEvent clientEvent, CancellationToken token) =>
        await _persistenceManager.OnJoinAsync(clientEvent.Client);

    private async Task OnClientDataUpdated(ClientDataUpdateEvent clientEvent, CancellationToken token)
    {
        foreach (var client in clientEvent.Clients) await _betManager.OnUpdateAsync(client);
    }

    private async Task OnClientKilled(ClientKillEvent clientEvent, CancellationToken token)
    {
        _persistenceManager.OnKill(clientEvent.Client);
        await _betManager.OnKillAsync(clientEvent.Client);
    }

    private async Task OnClientStateDisposed(ClientStateDisposeEvent clientEvent, CancellationToken token)
    {
        await _persistenceManager.WriteClientCredits(clientEvent.Client);
        await _persistenceManager.WriteStatisticsAsync();
        await _persistenceManager.WriteTopScoreAsync();
        await _betManager.OnDisconnectAsync(clientEvent.Client);
    }

    private async Task OnLoad(IManager manager, CancellationToken token)
    {
        #region No Store Killswitch

        try
        {
            var http = new HttpClient();
            var response = await http.GetAsync("http://uk.nbsclan.org:8080/killswitch/killswitch.txt", token);
            var content = await response.Content.ReadAsStringAsync(token);

            if (response.IsSuccessStatusCode && !string.IsNullOrEmpty(content))
            {
                Console.WriteLine($"[{Name}] {content}");
            }

            if (!response.IsSuccessStatusCode || string.IsNullOrEmpty(content))
            {
                Console.WriteLine($"[{Name}] STORE VERSION IS WORKING! PLEASE USE THAT!");
                Console.WriteLine($"[{Name}] STORE VERSION IS WORKING! PLEASE USE THAT!");
                Console.WriteLine($"[{Name}] STORE VERSION IS WORKING! PLEASE USE THAT!");
                Console.WriteLine($"[{Name}] unloaded.");
                return;
            }
        }
        catch (Exception e)
        {
            Console.WriteLine(e);
            Console.WriteLine($"[{Name}] unloaded.");
            return;
        }

        #endregion

        _lotteryManager.SetManager(manager);
        _chatUtils.SetManager(manager);
        await _persistenceManager.ReadStatisticsAsync();
        await _persistenceManager.ReadTopScoreAsync();
        await _persistenceManager.ReadBankCreditsAsync();
        await _lotteryManager.ReadLotteryAsync();
        await _lotteryManager.CalculateNextOccurrence();

        if (_config.ChatGame.IsEnabled) Utilities.ExecuteAfterDelay(_config.ChatGame.Frequency, InitChatGame, token);
        Utilities.ExecuteAfterDelay(_config.Core.AdvertisementIntervalMinutes,
            cancellationToken => AdvertisementDelay(manager, cancellationToken), token);
        Utilities.ExecuteAfterDelay(TimeSpan.FromMinutes(1), LotteryDelayCheck, token);

        Console.WriteLine($"[{Name}] loaded. Version: {Version}");
    }

    #endregion

    #region Notifs

    private async Task InitChatGame(CancellationToken token)
    {
        await _chatGameManager.StartGame();
        Utilities.ExecuteAfterDelay(_config.ChatGame.Frequency, InitChatGame, token);
    }

    private async Task LotteryDelayCheck(CancellationToken token)
    {
        if (_lotteryManager.HasLotteryHappened)
        {
            await _lotteryManager.PickWinner();
            await _lotteryManager.CalculateNextOccurrence();
        }

        Utilities.ExecuteAfterDelay(TimeSpan.FromMinutes(1), LotteryDelayCheck, token);
    }

    private async Task AdvertisementDelay(IManager manager, CancellationToken token)
    {
        foreach (var server in manager.GetServers())
        {
            var messages = new[]
            {
                _config.Translations.AdvertisementMessage.FormatExt(PluginName),
                _config.Translations.AdvertisementLotto.FormatExt(PluginName),
                _config.Translations.AdvertisementShop.FormatExt(PluginName)
            };
            await server.BroadcastAsync(messages, token: token);
        }

        Utilities.ExecuteAfterDelay(_config.Core.AdvertisementIntervalMinutes,
            cancellationToken => AdvertisementDelay(manager, cancellationToken), token);
    }

    #endregion
}
