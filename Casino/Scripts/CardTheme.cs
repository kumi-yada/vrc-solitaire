
using UdonSharp;
using UnityEngine;
using UnityEngine.UI;
using VRC.SDK3.Persistence;
using VRC.SDKBase;
using VRC.Udon;
using org.kumagee;

// One row in the card-back shop canvas: renders a single card back and equips
// it when its preview image is clicked. The shared state (unlocked backs,
// equipped back) lives in per-player PlayerData; any change is reported to
// Gacha, which refreshes every row.
public class CardTheme : UdonSharpBehaviour
{
    public const string UnlockedKey = "_RAINSTORM/CARD_THEME_UNLOCKED";
    public const string EquippedKey = "_RAINSTORM/CARD_THEME_EQUIPPED";
    public const string DarkKey = "_RAINSTORM/CARD_THEME_DARK";

    [Tooltip("Card back design this row shows. Locked rows render as a black silhouette and cannot be equipped.")]
    [SerializeField] private CardBack cardBack;

    [Tooltip("Button on the preview image. Clicking the image equips this card back when unlocked.")]
    [SerializeField] private Button imageButton;

    [Tooltip("Preview image showing this card back. Swaps to the dark variant when dark mode is on.")]
    [SerializeField] private Image previewImage;

    [Tooltip("Dark variant of the preview image sprite.")]
    [SerializeField] private Sprite darkSprite;

    [Tooltip("Gacha machine owning the shared state. Row changes are reported to it so it can update every other row.")]
    [SerializeField] private Gacha gacha;

    private bool _dataRestored = false;
    private int _unlockedFlags = 1 << (int)CardBack.White;
    private int _equipped = (int)CardBack.White;
    private Sprite _lightSprite;
    private bool _lightCaptured = false;

    // Read by Gacha when it builds the pull pool.
    public CardBack _GetBack()
    {
        return cardBack;
    }

    void Start()
    {
        _Refresh();
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

        _dataRestored = true;
        _Refresh();
    }

    // Wired to the preview image's button in CardTheme.prefab: clicking the
    // image equips this card back. Locked backs are silhouettes and do nothing.
    public void OnImageClick()
    {
        if (!_dataRestored)
        {
            return;
        }

        _SyncFromPlayerData();

        int backIndex = (int)cardBack;
        if ((_unlockedFlags & (1 << backIndex)) == 0)
        {
            return;
        }

        PlayerData.SetInt(EquippedKey, backIndex);
        _NotifyDecks();
        if (gacha != null)
        {
            gacha._BroadcastRefresh();
        }
    }

    // Re-reads the shared per-player state. The unlock/equip state lives in
    // PlayerData, not in these instances, and Gacha may have updated it — so
    // every row re-syncs before rendering.
    private void _SyncFromPlayerData()
    {
        VRCPlayerApi local = Networking.LocalPlayer;
        if (!Utilities.IsValid(local))
        {
            return;
        }

        if (PlayerData.TryGetInt(local, UnlockedKey, out int flags)) _unlockedFlags = flags;
        if (PlayerData.TryGetInt(local, EquippedKey, out int equipped)) _equipped = equipped;
    }

    // Called by Gacha after every pull, on restore, and by this row's own
    // events, so the row always renders fresh state.
    public void _Refresh()
    {
        if (_dataRestored)
        {
            _SyncFromPlayerData();
        }

        int mask = 1 << (int)cardBack;
        bool unlocked = (_unlockedFlags & mask) != 0;
        bool equipped = unlocked && _equipped == (int)cardBack;

        if (previewImage != null)
        {
            // Locked backs show as a black silhouette, the equipped one stays bright, the rest dimmed.
            previewImage.color = !unlocked ? Color.black : equipped ? Color.white : new Color(0.25f, 0.25f, 0.25f);
        }

        if (imageButton != null)
        {
            imageButton.interactable = unlocked && !equipped;
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
}
