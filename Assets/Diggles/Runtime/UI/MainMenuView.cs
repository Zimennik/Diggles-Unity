#nullable enable

namespace Diggles.Runtime
{
    using UnityEngine;
    using UnityEngine.UI;

    // Main menu — standard Unity uGUI Buttons, onClick logic wired in code; organised into three
    // panels (Main / Settings / Credits). Lay out a Canvas with the panels + buttons and assign them
    // in the Inspector. Registered + resolved via VContainer (no scene lookups); DigglesWorldRuntime
    // calls Show once the live 3D backdrop world is up.
    // New Game → tear down the backdrop world + build the campaign fresh, hand control to gameplay.
    // Settings / Credits → swap the Main panel for the respective panel (Back returns).
    // Load Game → placeholder until save/load lands. Exit → quit.
    public sealed class MainMenuView : MonoBehaviour
    {
        [Tooltip("CanvasGroup faded/blocked to show/hide the WHOLE menu. If null, the GameObject is toggled.")]
        [SerializeField] CanvasGroup? group;

        [Header("Panels")]
        [SerializeField] GameObject? mainPanel;
        [SerializeField] GameObject? settingsPanel;
        [SerializeField] GameObject? creditsPanel;

        [Header("Main-panel buttons")]
        [SerializeField] Button? newGameButton;
        [SerializeField] Button? loadGameButton;
        [SerializeField] Button? settingsButton;
        [SerializeField] Button? creditsButton;
        [SerializeField] Button? exitButton;

        // The native New Game sub-screen (menu → Neues Spiel → Einführung/Kampagne/Mehrspieler;
        // the exe boots single_Starttut.tcl / single_Startcamp.tcl for the first two —
        // Diggles.strings.txt:7772-7773). Lay out a panel with the three buttons + Back and
        // assign; message keys for the labels: Introduction / Campaign / Multiplayer / Back.
        // Until the panel is authored, New Game falls back to launching the campaign directly.
        [Header("New Game sub-screen")]
        [SerializeField] GameObject? newGamePanel;
        [SerializeField] Button? introductionButton;
        [SerializeField] Button? campaignButton;
        [SerializeField] Button? multiplayerButton;
        [SerializeField] Button? newGameBackButton;

        [Header("Sub-panel Back buttons")]
        [SerializeField] Button? settingsBackButton;
        [SerializeField] Button? creditsBackButton;

        DigglesWorldRuntime? _rt;

        void Awake()
        {
            if (newGameButton      != null) newGameButton.onClick.AddListener(OnNewGame);
            if (loadGameButton     != null) loadGameButton.onClick.AddListener(OnLoadGame);
            if (settingsButton     != null) settingsButton.onClick.AddListener(OnSettings);
            if (creditsButton      != null) creditsButton.onClick.AddListener(OnCredits);
            if (exitButton         != null) exitButton.onClick.AddListener(OnExit);
            if (introductionButton != null) introductionButton.onClick.AddListener(OnIntroduction);
            if (campaignButton     != null) campaignButton.onClick.AddListener(OnCampaign);
            if (multiplayerButton  != null) multiplayerButton.interactable = false;   // no MP in the port yet
            if (newGameBackButton  != null) newGameBackButton.onClick.AddListener(ShowMain);
            if (settingsBackButton != null) settingsBackButton.onClick.AddListener(ShowMain);
            if (creditsBackButton  != null) creditsBackButton.onClick.AddListener(ShowMain);
            ShowMain();
            Hide();
        }

        // Called by DigglesWorldRuntime after the backdrop world is built (it passes itself — no lookup).
        public void Show(DigglesWorldRuntime rt)
        {
            _rt = rt;
            ShowMain();
            if (group != null) { group.alpha = 1f; group.blocksRaycasts = true; group.interactable = true; }
            else gameObject.SetActive(true);
        }

        public void Hide()
        {
            if (group != null) { group.alpha = 0f; group.blocksRaycasts = false; group.interactable = false; }
            else if (gameObject != null) gameObject.SetActive(false);
        }

        void ShowMain()
        {
            if (mainPanel != null) mainPanel.SetActive(true);
            if (settingsPanel != null) settingsPanel.SetActive(false);
            if (creditsPanel != null) creditsPanel.SetActive(false);
            if (newGamePanel != null) newGamePanel.SetActive(false);
        }

        // ---- button handlers ----
        void OnNewGame()
        {
            if (newGamePanel != null)
            {
                if (mainPanel != null) mainPanel.SetActive(false);
                newGamePanel.SetActive(true);
                return;
            }
            // No authored sub-screen yet → the pre-sub-screen behavior (straight campaign).
            Hide();
            _rt?.NewGame(DigglesWorldRuntime.NewGameVariant.Campaign);
        }

        void OnIntroduction()
        {
            Hide();
            _rt?.NewGame(DigglesWorldRuntime.NewGameVariant.Introduction);
        }

        void OnCampaign()
        {
            Hide();
            _rt?.NewGame(DigglesWorldRuntime.NewGameVariant.Campaign);
        }

        void OnLoadGame() => Debug.Log("[MainMenu] Load Game — save/load not implemented yet.");

        void OnSettings()
        {
            if (mainPanel != null) mainPanel.SetActive(false);
            if (settingsPanel != null) settingsPanel.SetActive(true);
            else Debug.Log("[MainMenu] Settings — assign a Settings Panel.");
        }

        void OnCredits()
        {
            if (mainPanel != null) mainPanel.SetActive(false);
            if (creditsPanel != null) creditsPanel.SetActive(true);
            else Debug.Log("[MainMenu] Credits — assign a Credits Panel.");
        }

        void OnExit() => Application.Quit();
    }
}
