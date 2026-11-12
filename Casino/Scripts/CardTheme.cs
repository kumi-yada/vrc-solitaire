
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Persistence;
using VRC.SDKBase;
using VRC.Udon;
using org.kumagee;
using UCS;

public class CardTheme : UdonSharpBehaviour
{
    public const string UnlockedKey = "_RAINSTORM/CARD_THEME_UNLOCKED";
    public const string EquippedKey = "_RAINSTORM/CARD_THEME_EQUIPPED";
    public const string DarkKey = "_RAINSTORM/CARD_THEME_DARK";
    // Pulls-since-last-rare-or-better counter. A pull at PityEvery or beyond is
    // forced to drop Dual or Gold.
    public const string PityKey = "_RAINSTORM/GACHA_PITY";

    // One gacha pull costs this many chips, no matter which locked row's button
    // was pressed.
    private const int GachaPullPrice = 500;
    private const int PityEvery = 10;

    [Tooltip("Card back design this row shows. Locked rows act as gacha entries: their button pulls the machine instead of buying this specific back.")]
    [SerializeField] private CardBack cardBack;

    [Tooltip("Legacy direct-buy price, kept serialized for scene compatibility. Unused since the gacha change - pulls cost GachaPullPrice.")]
    [SerializeField] private int price = 5000;

    [Tooltip("Button the player clicks to buy or equip this card back.")]
    [SerializeField] private Button buyButton;

    [Tooltip("Button label. Text is managed automatically.")]
    [SerializeField] private LocalizedText buttonLabel;

    [Tooltip("Chips wallet used to pay for the card back.")]
    [SerializeField] private UdonChips udonChips;

    [Tooltip("Label color for unlocked (buyable/usable) card backs.")]
    [SerializeField] private Color whiteColor = Color.white;

    [Tooltip("Preview image showing this card back. Swaps to the dark variant when dark mode is on.")]
    [SerializeField] private Image previewImage;

    [Tooltip("Dark variant of the preview image sprite.")]
    [SerializeField] private Sprite darkSprite;

    private bool _dataRestored = false;
    private int _unlockedFlags = 1 << (int)CardBack.White;
    private int _equipped = (int)CardBack.White;
    private int _pity = 0;
    private Sprite _lightSprite;
    private bool _lightCaptured = false;
    private Notification _notification;
    private LocalizationManager _localization;

    void Start()
    {
        if (udonChips == null)
        {
            udonChips = GameObject.Find("UdonChips").GetComponent<UdonChips>();
        }

        if (_notification == null)
        {
            GameObject n = GameObject.Find("Notification");
            if (n != null) _notification = n.GetComponent<Notification>();
        }

        if (_localization == null)
        {
            GameObject l = GameObject.Find("Localization");
            if (l != null) _localization = l.GetComponent<LocalizationManager>();
        }

        Refresh();
    }

    public override void OnPlayerRestored(VRCPlayerApi player)
    {
        if (player == null || !player.IsValid() || !player.isLocal)
        {
            return;
        }

        if (PlayerData.TryGetInt(player, UnlockedKey, out int flags))
        {
            _unlockedFlags = flags;
        }

        if (PlayerData.TryGetInt(player, EquippedKey, out int equipped))
        {
            _equipped = equipped;
        }

        if (PlayerData.TryGetInt(player, PityKey, out int pity))
        {
            _pity = pity;
        }

        _dataRestored = true;
        Refresh();
    }

    // Button handler name is wired in Button.prefab, so it stays as is. An
    // unlocked row equips its back; a locked row pulls the gacha machine.
    public void OnBuyClick()
    {
        if (!_dataRestored)
        {
            return;
        }

        int backIndex = (int)cardBack;
        int mask = 1 << backIndex;

        if ((_unlockedFlags & mask) != 0)
        {
            PlayerData.SetInt(EquippedKey, backIndex);
            _BroadcastEquipped(backIndex);
            _NotifyDecks();
        }
        else
        {
            _Pull();
        }
    }

    // One gacha pull: pay, roll a back from the whole non-White pool (commons
    // Blue..Green 80%, rare Dual 15%, epic Gold 5%), then unlock it or report a
    // duplicate. Every PityEvery-th pull is forced to rare-or-better, preferring a
    // rare+ back that is still locked. Everything is per-player PlayerData, so the
    // rolling client is the only one that needs to agree with itself.
    private void _Pull()
    {
        if (udonChips == null || udonChips.money < GachaPullPrice)
        {
            _Notify(_Text("NOTIFY_NOT_ENOUGH_CHIPS") + GachaPullPrice + "c.");
            return;
        }
        udonChips.money -= GachaPullPrice;

        _pity++;
        int back;
        if (_pity >= PityEvery)
        {
            bool dualLocked = (_unlockedFlags & (1 << (int)CardBack.Dual)) == 0;
            bool goldLocked = (_unlockedFlags & (1 << (int)CardBack.Gold)) == 0;
            if (dualLocked && goldLocked) back = Random.Range(0, 2) == 0 ? (int)CardBack.Gold : (int)CardBack.Dual;
            else if (dualLocked) back = (int)CardBack.Dual;
            else if (goldLocked) back = (int)CardBack.Gold;
            else back = Random.Range(0, 2) == 0 ? (int)CardBack.Gold : (int)CardBack.Dual;
            _pity = 0;
        }
        else
        {
            int roll = Random.Range(0, 100);
            if (roll < 80) back = Random.Range((int)CardBack.Blue, (int)CardBack.Dual); // Blue..Green
            else if (roll < 95) back = (int)CardBack.Dual;
            else back = (int)CardBack.Gold;
            if (back == (int)CardBack.Dual || back == (int)CardBack.Gold) _pity = 0;
        }
        PlayerData.SetInt(PityKey, _pity);

        int mask = 1 << back;
        if ((_unlockedFlags & mask) != 0)
        {
            _Notify(_Text("GACHA_DUPLICATE") + _BackName(back));
        }
        else
        {
            _unlockedFlags |= mask;
            PlayerData.SetInt(UnlockedKey, _unlockedFlags);
            _Notify(_Text("GACHA_NEW") + _BackName(back));
            if (_AllUnlocked()) _Notify(_Text("GACHA_COMPLETE"));
        }

        _BroadcastRefresh();
    }

    private bool _AllUnlocked()
    {
        // Every back except the free default White.
        const int gachaMask = (1 << (int)CardBack.White) - 1;
        return (_unlockedFlags & gachaMask) == gachaMask;
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
        if (_localization == null) return key;
        return _localization.GetText(key);
    }

    private void _Notify(string message)
    {
        if (_notification != null) _notification.Notify(message);
    }

    private void _BroadcastRefresh()
    {
        Transform parent = transform.parent;
        if (parent == null)
        {
            Refresh();
            return;
        }

        for (int i = 0; i < parent.childCount; i++)
        {
            CardTheme theme = parent.GetChild(i).GetComponent<CardTheme>();
            if (theme != null)
            {
                theme.Refresh();
            }
        }
    }

    // The equipped back lives on the player's own deck (a PlayerObject), so update
    // every deck this player carries. DeckManager._RefreshCardBack reads the freshly
    // written PlayerData and syncs it to the other clients.
    private void _NotifyDecks()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local)) return;

        GameObject[] objects = Networking.GetPlayerObjects(local);
        if (objects == null) return;

        for (int i = 0; i < objects.Length; i++)
        {
            if (objects[i] == null) continue;

            DeckManager[] decks = objects[i].GetComponentsInChildren<DeckManager>(true);
            if (decks == null) continue;

            for (int d = 0; d < decks.Length; d++)
            {
                if (decks[d] != null) decks[d]._RefreshCardBack();
            }
        }
    }

    private void _BroadcastEquipped(int equipped)
    {
        Transform parent = transform.parent;
        if (parent == null)
        {
            return;
        }

        for (int i = 0; i < parent.childCount; i++)
        {
            CardTheme theme = parent.GetChild(i).GetComponent<CardTheme>();
            if (theme != null)
            {
                theme._SetEquipped(equipped);
            }
        }
    }

    public void _SetEquipped(int equipped)
    {
        _equipped = equipped;
        Refresh();
    }

    // Swaps the preview image between the default and dark sprite. The default is
    // captured lazily on first call so it stays correct regardless of whether the
    // dark toggle arrives before or after this behaviour's Start.
    public void _SetDark(bool dark)
    {
        if (previewImage == null)
        {
            return;
        }

        if (!_lightCaptured)
        {
            _lightSprite = previewImage.sprite;
            _lightCaptured = true;
        }

        if (dark)
        {
            if (darkSprite != null)
            {
                previewImage.sprite = darkSprite;
            }
        }
        else
        {
            previewImage.sprite = _lightSprite;
        }
    }

    private void Refresh()
    {
        int mask = 1 << (int)cardBack;
        bool unlocked = (_unlockedFlags & mask) != 0;
        bool equipped = unlocked && _equipped == (int)cardBack;

        if (buyButton != null)
        {
            buyButton.interactable = !equipped;
        }

        if (buttonLabel == null)
        {
            return;
        }

        buttonLabel.TextElement.color = equipped ? Color.gray : whiteColor;

        if (!unlocked)
        {
            buttonLabel.SetPrefixKeyAndSuffix("", "GACHA_PULL", " (" + GachaPullPrice + "c)");
        }
        else if (equipped)
        {
            buttonLabel.SetPrefixKeyAndSuffix("", "EQUIPPED", "");
        }
        else
        {
            buttonLabel.SetPrefixKeyAndSuffix("", "USE", "");
        }
    }
}
