
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Components;
using VRC.SDK3.Persistence;
using VRC.SDKBase;
using org.kumagee;
using UCS;

// Rarity of a card back, set per CardTheme row in the Inspector; drives its
// gacha pull chance. Top-level because U# does not support nested types.
public enum Rarity
{
    Common,
    Uncommon,
    Rare,
    Epic,
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
    // The reset countdown is shown in quarter-hour steps (02:00, 01:45, ...).
    private const int ResetStepSeconds = 900;
    // Each pull made today shifts 1% of pull chance from Common to the higher
    // rarities (Uncommon and up). Weights are in per-mille, so 1% is 10.
    private const int DailyLuckBonus = 10;
    // Supporters shift double: 2% (20 per-mille) per pull.
    private const int SupporterLuckBonus = 20;

    // Pull chance per rarity, in per-mille (1/1000), before the daily luck
    // bonus. A pull rolls a rarity first, then a random card back of that
    // rarity. Per-mille lets Legendary sit at 0.1%, which whole percents
    // cannot express.
    private const int CommonWeight = 700;
    private const int UncommonWeight = 200;
    private const int RareWeight = 80;
    private const int EpicWeight = 19;
    private const int LegendaryWeight = 1;

    [Tooltip("Chips wallet used to pay for gacha pulls.")]
    [SerializeField] private UdonChips udonChips;
    [Tooltip("Error popups such as not enough chips. Pull results are shown by the pullResultImage overlay and by scrolling to the pulled row.")]
    [SerializeField] private Notification notification;
    [SerializeField] private LocalizationManager localization;
    [SerializeField] private LocalizedText gachaStatusLabel;
    [Tooltip("Label showing the time left until the daily pulls and price reset, as hh:mm.")]
    [SerializeField] private LocalizedText gachaResetLabel;
    [Tooltip("Optional supporter list. Supporters get +2% luck per pull instead of +1%.")]
    [SerializeField] private Supporters supporters;
    [Tooltip("Every CardTheme prefab instance in the shop canvas.")]
    [SerializeField] private GameObject[] cardThemeObjects;
    [Tooltip("Scroll container holding the CardTheme rows. After a pull it scrolls so the pulled row is visible.")]
    [SerializeField] private ScrollRect cardScroll;
    [Tooltip("Overlay image in the shop canvas, shown in front of the card-back list for a moment after a pull. Last child of the panel so it renders on top; raycast target off so it never blocks the rows.")]
    [SerializeField] private Image pullResultImage;

    [Tooltip("Price of the first pull of a day in chips. Every further pull that day doubles it.")]
    public int gachaBasePrice = 5000;
    [Tooltip("Seconds the pulled card back stays visible.")]
    [SerializeField] private float pullResultDuration = 2f;
    [Tooltip("Scale the pull result pops in from before settling at 1.")]
    [SerializeField] private float pullResultPopScale = 0.5f;
    [Tooltip("Duration of the pop-in and shrink-out tweens in seconds.")]
    [SerializeField] private float pullResultTweenDuration = 0.2f;

    private bool _dataRestored = false;
    private int _unlockedFlags = 1 << (int)CardBack.White;
    private int _day = -1;
    private int _pullsToday = 0;
    private LocalizedText _gachaLabel;
    private CardTheme[] _themes;
    private int _lastResetStep = -1;
    private VRCTweenHandle _popTween;
    private VRCTweenHandle _hideTween;
    private VRCTweenHandle _hideDelay;

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

    void Update()
    {
        // Repaint only when the displayed quarter-hour changes; every repaint
        // of a LocalizedText rebuilds the TMP text.
        if (_ResetStep() != _lastResetStep)
        {
            _RefreshResetLabel();
        }
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
    // from the whole non-White pool, unlock it if new, and scroll the card
    // list to the pulled row either way. Every pull also counts toward
    // today's escalating price and luck. Everything is per-player PlayerData,
    // so the rolling client is the only one that needs to agree with itself.
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
        _ScrollToBack(back);
        _ShowPullResult(back);

        _RefreshLabels();
        _BroadcastRefresh();
    }

    // Shows the pulled card back as a temporary overlay in front of the
    // card-back list: pops in with a small scale tween, then shrinks back out
    // and deactivates after pullResultDuration. Re-pulling kills the pending
    // hide so the overlay just swaps sprite and re-pops.
    private void _ShowPullResult(int back)
    {
        if (pullResultImage == null)
        {
            return;
        }

        CardTheme[] themes = _Themes();
        int index = _IndexOfBack(back);
        if (index < 0 || themes == null || themes[index] == null)
        {
            return;
        }

        Sprite sprite = themes[index]._GetPreviewSprite();
        if (sprite == null)
        {
            return;
        }

        _popTween.Kill();
        _hideTween.Kill();
        _hideDelay.Kill();

        pullResultImage.sprite = sprite;
        GameObject overlay = pullResultImage.gameObject;
        overlay.SetActive(true);
        overlay.transform.localScale = Vector3.one * pullResultPopScale;
        _popTween = overlay.transform.TweenScale(Vector3.one, pullResultTweenDuration, VRCTweenEase.OutBack);
        _hideDelay = VRCTween.DelayedCall(this, nameof(_HidePullResult), pullResultDuration);
    }

    // Shrinks the overlay back out; the delayed call deactivates it once the
    // tween is done (same kill-and-delay pattern as IntroCard).
    public void _HidePullResult()
    {
        _hideTween.Kill();
        _hideDelay.Kill();
        if (pullResultImage == null || !pullResultImage.gameObject.activeSelf)
        {
            return;
        }
        _hideTween = pullResultImage.transform.TweenScale(Vector3.zero, pullResultTweenDuration, VRCTweenEase.InCubic);
        _hideDelay = VRCTween.DelayedCall(this, nameof(_FinishHidePullResult), pullResultTweenDuration);
    }

    public void _FinishHidePullResult()
    {
        if (pullResultImage != null)
        {
            pullResultImage.gameObject.SetActive(false);
        }
    }

    void OnDestroy()
    {
        _popTween.Kill();
        _hideTween.Kill();
        _hideDelay.Kill();
    }

    // Scrolls the card-back list so the pulled row is centered in the
    // viewport; the row itself shows the result (unlock state, later its
    // rarity background color). Corner-based, so it works no matter how the
    // rows are laid out inside the content.
    private void _ScrollToBack(int back)
    {
        if (cardScroll == null)
        {
            return;
        }

        RectTransform content = cardScroll.content;
        if (content == null)
        {
            return;
        }

        RectTransform viewport = cardScroll.viewport;
        if (viewport == null)
        {
            viewport = (RectTransform)cardScroll.transform;
        }

        int index = _IndexOfBack(back);
        if (index < 0 || cardThemeObjects[index] == null)
        {
            return;
        }

        float scrollable = content.rect.width - viewport.rect.width;
        if (scrollable <= 0f)
        {
            return;
        }

        RectTransform item = (RectTransform)cardThemeObjects[index].transform;
        Vector3[] corners = new Vector3[4];
        item.GetWorldCorners(corners);
        // Corner 0 is bottom-left and corner 2 top-right, in world space.
        float itemCenter = (content.InverseTransformPoint(corners[0]).x + content.InverseTransformPoint(corners[2]).x) * 0.5f;

        // horizontalNormalizedPosition 1 shows the left edge of the content,
        // so the visible window starts at offset (1 - pos) * scrollable.
        float offset = Mathf.Clamp(itemCenter - viewport.rect.width * 0.5f, 0f, scrollable);

        cardScroll.StopMovement();
        cardScroll.horizontalNormalizedPosition = 1f - offset / scrollable;
    }

    // Index of the wired row showing the given card back, or -1.
    private int _IndexOfBack(int back)
    {
        if (cardThemeObjects == null)
        {
            return -1;
        }
        for (int i = 0; i < cardThemeObjects.Length; i++)
        {
            if (cardThemeObjects[i] == null)
            {
                continue;
            }
            CardTheme theme = cardThemeObjects[i].GetComponent<CardTheme>();
            if (theme != null && theme._GetBack() == (CardBack)back)
            {
                return i;
            }
        }
        return -1;
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

        // The bonus moves chance from Common to the higher rarities, split by
        // their base weights; the total stays 1000. Legendary gets the rest.
        int bonus = _LuckBonus();
        int higherTotal = UncommonWeight + RareWeight + EpicWeight + LegendaryWeight;
        int commonWeight = CommonWeight - bonus;
        int uncommonWeight = UncommonWeight + bonus * UncommonWeight / higherTotal;
        int rareWeight = RareWeight + bonus * RareWeight / higherTotal;
        int epicWeight = EpicWeight + bonus * EpicWeight / higherTotal;
        int legendaryWeight = 1000 - commonWeight - uncommonWeight - rareWeight - epicWeight;

        int roll = Random.Range(0, 1000);
        Rarity target;
        if (roll < commonWeight) target = Rarity.Common;
        else if (roll < commonWeight + uncommonWeight) target = Rarity.Uncommon;
        else if (roll < commonWeight + uncommonWeight + rareWeight) target = Rarity.Rare;
        else if (roll < commonWeight + uncommonWeight + rareWeight + epicWeight) target = Rarity.Epic;
        else target = Rarity.Legendary;

        int count = 0;
        for (int i = 0; i < themes.Length; i++)
        {
            if (themes[i] != null && themes[i]._GetBack() != CardBack.White && themes[i].rarity == target)
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
            if (themes[i] != null && themes[i]._GetBack() != CardBack.White && themes[i].rarity == target)
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

    private string _Text(string key)
    {
        if (localization == null) return key;
        return localization.GetText(key);
    }

    private void _Notify(string message)
    {
        if (notification != null) notification.Notify(message);
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

        if (gachaStatusLabel != null)
        {
            gachaStatusLabel.SetPrefixKeyAndSuffix(_Text("GACHA_PULLS_TODAY") + _pullsToday + " (", "GACHA_LUCK_BONUS", (_LuckBonus() / 10) + "%)");
        }

        _RefreshResetLabel();
    }

    // Quarter-hour steps left until the UTC day boundary. The label shows the
    // floored step, so it ticks down 02:00 -> 01:45 -> 01:30 ...
    private int _ResetStep()
    {
        // Udon exposes long division but not long modulo, so reduce to int
        // seconds first and take the modulo there.
        int nowSec = (int)(Networking.GetServerTimeInMilliseconds() / 1000L);
        int remainingSec = 86400 - (nowSec % 86400);
        if (remainingSec >= 86400)
        {
            remainingSec = 0;
        }
        return remainingSec / ResetStepSeconds;
    }

    private void _RefreshResetLabel()
    {
        int step = _ResetStep();
        _lastResetStep = step;
        if (gachaResetLabel == null)
        {
            return;
        }
        gachaResetLabel.SetPrefixKeyAndSuffix(_Text("GACHA_RESET_IN") + _FormatResetTime(step), "", "");
    }

    private string _FormatResetTime(int step)
    {
        int total = step * ResetStepSeconds;
        int h = total / 3600;
        int m = (total % 3600) / 60;
        return (h < 10 ? "0" + h : h.ToString()) + ":" + (m < 10 ? "0" + m : m.ToString());
    }

    // Effective higher-rarity (Rare and Legendary) luck bonus in per-mille for
    // the pulls made today: +1% (10) per pull, +2% (20) for supporters, capped
    // so Common never goes below 0%.
    private int _LuckBonus()
    {
        int bonus = _pullsToday * (_IsLocalSupporter() ? SupporterLuckBonus : DailyLuckBonus);
        if (bonus > CommonWeight)
        {
            bonus = CommonWeight;
        }
        return bonus;
    }

    private bool _IsLocalSupporter()
    {
        if (supporters == null) return false;
        return supporters.IsSupporter(Networking.LocalPlayer);
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
