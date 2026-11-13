
using UdonSharp;
using UnityEngine;
using VRC.SDK3.Persistence;
using VRC.SDKBase;
using org.kumagee;
using UCS;

// Rarity of a card back; drives its gacha pull chance. Top-level because U#
// does not support nested type declarations.
public enum Rarity
{
    Common,
    Rare,
    Legendary,
}

// The gacha machine behind the single "Gacha" button next to the dark mode
// toggle in the shop canvas. Owns all gacha state (unlocked backs, daily pull
// count, escalating price/luck) in per-player PlayerData and refreshes the
// CardTheme rows after every pull. The button's onClick calls PullGacha on
// this behaviour, so the scene no longer needs to reference a CardTheme
// instance for pulling.
public class Gacha : UdonSharpBehaviour
{
    // UTC day (server time in ms / 1 day) of the last saved daily gacha state,
    // and the number of pulls made that day. Both reset when the day changes.
    public const string DayKey = "_RAINSTORM/GACHA_DAY";
    public const string PullsKey = "_RAINSTORM/GACHA_DAILY_PULLS";

    // Cap the doubling so a large base price cannot overflow int.
    private const int MaxPriceShift = 18;
    // Each pull made today shifts 1% of pull chance from Common to the higher
    // rarities (Rare and Legendary). Weights are in per-mille, so 1% is 10.
    private const int DailyLuckBonus = 10;

    // Pull chance per rarity, in per-mille (1/1000), before the daily luck
    // bonus. A pull rolls a rarity first, then a random card back of that
    // rarity. Per-mille lets Legendary sit at 0.1%, which whole percents
    // cannot express.
    private const int CommonWeight = 900;
    private const int RareWeight = 99;
    private const int LegendaryWeight = 1;

    [Tooltip("Chips wallet used to pay for gacha pulls.")]
    [SerializeField] private UdonChips udonChips;
    [SerializeField] private Notification notification;
    [Tooltip("Clip played with the pull-result notification instead of the Notification's default clip.")]
    [SerializeField] private AudioClip gachaPullClip;
    [SerializeField] private LocalizationManager localization;
    [SerializeField] private LocalizedText gachaStatusLabel;
    [Tooltip("Every CardTheme prefab instance in the shop canvas.")]
    [SerializeField] private GameObject[] cardThemeObjects;

    [Tooltip("Price of the first pull of a day in chips. Every further pull that day doubles it.")]
    public int gachaBasePrice = 5000;

    private bool _dataRestored = false;
    private int _unlockedFlags = 1 << (int)CardBack.White;
    private int _day = -1;
    private int _pullsToday = 0;
    private LocalizedText _gachaLabel;
    private CardTheme[] _themes;

    void Start()
    {
        // Registered so a language switch re-bakes the status line: its prefix
        // holds translated text, which LocalizedText never re-translates on its
        // own.
        if (localization != null) localization.Register(this);

        _RefreshLabels();
    }

    // Called by the LocalizationManager on every language change. The status
    // line mixes a translated prefix with live numbers, so it has to be rebuilt
    // in the new language.
    public void _OnLanguageChanged()
    {
        _RefreshLabels();
    }

    public override void OnPlayerRestored(VRCPlayerApi player)
    {
        if (player == null || !player.IsValid() || !player.isLocal)
        {
            return;
        }

        if (PlayerData.TryGetInt(player, CardTheme.UnlockedKey, out int flags))
        {
            _unlockedFlags = flags;
        }

        if (PlayerData.TryGetInt(player, DayKey, out int day))
        {
            _day = day;
        }

        if (PlayerData.TryGetInt(player, PullsKey, out int pulls))
        {
            _pullsToday = pulls;
        }

        _dataRestored = true;
        _CheckDayReset();
        _RefreshLabels();
        _BroadcastRefresh();
    }

    // Wired to the Gacha button's onClick: one pull pays the current daily
    // price and returns a random card back, including ones the player already
    // owns.
    public void PullGacha()
    {
        if (!_dataRestored)
        {
            return;
        }

        _CheckDayReset();
        _SyncFromPlayerData();
        _Pull();
    }

    // One gacha pull: pay the current daily price, roll a rarity by its
    // weight (plus today's luck bonus), then a random back of that rarity
    // from the whole non-White pool, unlock it if new, and notify the pulled
    // back's name either way. Every pull also counts toward today's
    // escalating price and luck. Everything is per-player PlayerData, so the
    // rolling client is the only one that needs to agree with itself.
    private void _Pull()
    {
        int price = _CurrentPrice();
        if (udonChips == null || udonChips.money < price)
        {
            _Notify(_Text("NOTIFY_NOT_ENOUGH_CHIPS") + price + "c.");
            return;
        }
        udonChips.money -= price;

        int back = _RollBack();

        _pullsToday++;
        PlayerData.SetInt(PullsKey, _pullsToday);

        int mask = 1 << back;
        if ((_unlockedFlags & mask) == 0)
        {
            _unlockedFlags |= mask;
            PlayerData.SetInt(CardTheme.UnlockedKey, _unlockedFlags);
        }
        _Notify(_BackName(back), gachaPullClip);

        _RefreshLabels();
        _BroadcastRefresh();
    }

    // Each card back has a rarity that drives its pull chance.
    public static Rarity GetRarity(CardBack back)
    {
        switch (back)
        {
            case CardBack.Gold: return Rarity.Legendary;
            case CardBack.Dual: return Rarity.Rare;
            default: return Rarity.Common;
        }
    }

    // Rolls one card back from the whole pool: every wired CardTheme row,
    // except the free default White. Each pull made today shifts part of the
    // chance from Common to the higher rarities (see _LuckBonus).
    private int _RollBack()
    {
        CardTheme[] themes = _Themes();
        if (themes == null)
        {
            return (int)CardBack.Blue;
        }

        // The bonus moves chance from Common to Rare and Legendary, split by
        // their base weights; the total stays 1000. Legendary gets the rest.
        int bonus = _LuckBonus();
        int commonWeight = CommonWeight - bonus;
        int rareWeight = RareWeight + bonus * RareWeight / (RareWeight + LegendaryWeight);

        int roll = Random.Range(0, 1000);
        Rarity target;
        if (roll < commonWeight) target = Rarity.Common;
        else if (roll < commonWeight + rareWeight) target = Rarity.Rare;
        else target = Rarity.Legendary;

        int count = 0;
        for (int i = 0; i < themes.Length; i++)
        {
            if (themes[i] != null && themes[i]._GetBack() != CardBack.White && GetRarity(themes[i]._GetBack()) == target)
            {
                count++;
            }
        }

        if (count == 0)
        {
            return (int)CardBack.Blue;
        }

        int pick = Random.Range(0, count);
        for (int i = 0; i < themes.Length; i++)
        {
            if (themes[i] != null && themes[i]._GetBack() != CardBack.White && GetRarity(themes[i]._GetBack()) == target)
            {
                if (pick == 0)
                {
                    return (int)themes[i]._GetBack();
                }
                pick--;
            }
        }

        return (int)CardBack.Blue;
    }

    private string _BackName(int back)
    {
        switch (back)
        {
            case (int)CardBack.Gold: return _Text("BACK_GOLD");
            case (int)CardBack.Blue: return _Text("BACK_BLUE");
            case (int)CardBack.Red: return _Text("BACK_RED");
            case (int)CardBack.Yellow: return _Text("BACK_YELLOW");
            case (int)CardBack.Green: return _Text("BACK_GREEN");
            case (int)CardBack.Dual: return _Text("BACK_DUAL");
            default: return _Text("BACK_WHITE");
        }
    }

    private string _Text(string key)
    {
        if (localization == null) return key;
        return localization.GetText(key);
    }

    private void _Notify(string message)
    {
        _Notify(message, null);
    }

    // A null clip makes the Notification fall back to its default sound.
    private void _Notify(string message, AudioClip clip)
    {
        if (notification != null) notification.Notify(message, clip);
    }

    // The gacha button's own label shows the price of the next pull; the
    // status line under it shows today's pull count and luck bonus.
    private void _RefreshLabels()
    {
        if (_gachaLabel == null)
        {
            _gachaLabel = gameObject.GetComponentInChildren<LocalizedText>(true);
            if (_gachaLabel == null)
            {
                return;
            }
        }

        _gachaLabel.SetPrefixKeyAndSuffix("", "GACHA_PULL", " (" + _CurrentPrice() + "c)");

        if (gachaStatusLabel == null)
        {
            return;
        }

        gachaStatusLabel.SetPrefixKeyAndSuffix(_Text("GACHA_PULLS_TODAY") + _pullsToday + " (", "GACHA_LUCK_BONUS", (_LuckBonus() / 10) + "%)");
    }

    // Effective higher-rarity (Rare and Legendary) luck bonus in per-mille for
    // the pulls made today: +1% (10) per pull, capped so Common never goes
    // below 0%.
    private int _LuckBonus()
    {
        int bonus = _pullsToday * DailyLuckBonus;
        if (bonus > CommonWeight)
        {
            bonus = CommonWeight;
        }
        return bonus;
    }

    // Price of the next pull: the base price doubled once per pull made today.
    private int _CurrentPrice()
    {
        int shift = _pullsToday;
        if (shift > MaxPriceShift)
        {
            shift = MaxPriceShift;
        }
        return gachaBasePrice << shift;
    }

    // Daily gacha state resets at the UTC day boundary (server time). Called on
    // restore and before every pull, so a player sitting across midnight gets a
    // fresh price and luck.
    private void _CheckDayReset()
    {
        int today = (int)(Networking.GetServerTimeInMilliseconds() / 86400000L);
        if (_day == today)
        {
            return;
        }

        _day = today;
        _pullsToday = 0;
        PlayerData.SetInt(DayKey, _day);
        PlayerData.SetInt(PullsKey, 0);
    }

    // Re-reads the shared per-player gacha state, so a pull always starts from
    // fresh values.
    private void _SyncFromPlayerData()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local))
        {
            return;
        }

        if (PlayerData.TryGetInt(local, CardTheme.UnlockedKey, out int flags)) _unlockedFlags = flags;
        if (PlayerData.TryGetInt(local, DayKey, out int day)) _day = day;
        if (PlayerData.TryGetInt(local, PullsKey, out int pulls)) _pullsToday = pulls;
    }

    // The CardTheme components of the wired rows, resolved once. The rows are
    // assigned through cardThemeObjects in the Inspector, so no scene-wide
    // lookup is needed.
    private CardTheme[] _Themes()
    {
        if (_themes == null && cardThemeObjects != null)
        {
            _themes = new CardTheme[cardThemeObjects.Length];
            for (int i = 0; i < cardThemeObjects.Length; i++)
            {
                if (cardThemeObjects[i] != null)
                {
                    _themes[i] = cardThemeObjects[i].GetComponent<CardTheme>();
                }
            }
        }
        return _themes;
    }

    // After a pull, or when a CardTheme row reports a change, every row
    // re-renders (silhouette / equipped state) from the freshly written
    // PlayerData.
    public void _BroadcastRefresh()
    {
        CardTheme[] themes = _Themes();
        if (themes == null)
        {
            return;
        }
        for (int i = 0; i < themes.Length; i++)
        {
            if (themes[i] != null)
            {
                themes[i]._Refresh();
            }
        }
    }

    // Dark mode reported by the DarkToggle: swap every row's preview sprite
    // between the default and the dark variant.
    public void _BroadcastDark(bool dark)
    {
        CardTheme[] themes = _Themes();
        if (themes == null)
        {
            return;
        }
        for (int i = 0; i < themes.Length; i++)
        {
            if (themes[i] != null)
            {
                themes[i]._SetDark(dark);
            }
        }
    }
}
