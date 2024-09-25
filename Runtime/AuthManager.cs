using UnityEngine;
using Unity.Services.Core;
// using Unity.Services.Authentication;
// Live2D

#if UNITY_ANDROID
using GooglePlayGames;
using GooglePlayGames.BasicApi;
#endif

// External dependencies
using AppleAuth;
using AppleAuth.Enums;
using AppleAuth.Interfaces;
using AppleAuth.Native;
using System;
using System.Text;
using System.Threading.Tasks;
using Unity.Services.Authentication;
using UnityEngine.Events;
using UnityEngine.Serialization;

// using AuthenticationException = System.Security.Authentication.AuthenticationException;


namespace Pickiss
{
    public class AuthManager : MonoBehaviour
    {
        [FormerlySerializedAs("PlayerExternalIds")] public string playerExternalIds;
        public bool hasUnityIdentifier = false; // 유니티 식별자 받아왔음!
        public bool doneUnityAuthentication = false; // 유니티 인증 통신 완료  (실패시에도 true)
        
        # region SystemManager에서 받은 Action

        private Action<string> _onErrorPopup;
        private Action _onRefresh;
        private Action<UnityAction> _onConfirmPopup;
        #endregion
        
        private static AuthManager _instance;

        public static AuthManager Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = FindObjectOfType<AuthManager>();
                    if (_instance == null)
                    {
                        GameObject singleton = new GameObject(typeof(AuthManager).ToString());
                        _instance = singleton.AddComponent<AuthManager>();
                        DontDestroyOnLoad(singleton);
                    }
                }

                return _instance;
            }
        }

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                Destroy(this.gameObject);
                return;
            }

            _instance = this;
            DontDestroyOnLoad(gameObject);

            InitGooglePlayService();
        }

        public async Task Init(
            Action<string> onErrorPopup,
            Action onRefresh,
            Action<UnityAction> onConfirmPopup
            )
        {
            _onErrorPopup = onErrorPopup;
            _onRefresh = onRefresh;
            _onConfirmPopup = onConfirmPopup;

#if UNITY_ANDROID
            PlayGamesPlatform.Activate();
#endif
            

            SetupUnityGameServiceEvents();
            await SignInUnityGameServiceAnonymouslyAsync();
        }

        private void SetupUnityGameServiceEvents()
        {
            AuthenticationService.Instance.SignedIn += () =>
            {
                // Shows how to get a playerID
                Debug.Log($"UGS PlayerID: {AuthenticationService.Instance.PlayerId}");

                // Shows how to get an access token
                Debug.Log($"UGS Access Token: {AuthenticationService.Instance.AccessToken}");
            };

            AuthenticationService.Instance.SignInFailed += (err) =>
            {
                Debug.LogError(err);
            };

            AuthenticationService.Instance.SignedOut += () =>
            {
                Debug.Log("UGS Player signed out.");
            };

            AuthenticationService.Instance.Expired += () =>
            {
                Debug.Log("UGS Player session could not be refreshed and expired.");
            };


#if UNITY_IOS
            InitAppleSignIn();
#endif
        }


        private async Task SignInUnityGameServiceAnonymouslyAsync()
        {
            try
            {
                await AuthenticationService.Instance.SignInAnonymouslyAsync();
                PlayerPrefs.SetInt("userLoginExists", 1); // 로그인을 한번이라도 했으면  1로 적용한다. 

                await GetPlayerInfoAsync();
                hasUnityIdentifier = true;
                doneUnityAuthentication = true;
            }
            catch (Unity.Services.Authentication.AuthenticationException ex)
            {
                // Compare error code to AuthenticationErrorCodes
                // Notify the player with the proper error message
                Debug.LogException(ex);
                hasUnityIdentifier = false;

                // not valid의 경우. 예외처리
                if (!string.IsNullOrEmpty(ex.Message) && ex.Message.Contains("session token is not valid"))
                {
                    AuthenticationService.Instance.ClearSessionToken();
                    await SignInUnityGameServiceAnonymouslyAsync();
                    return;
                }
                
                _onErrorPopup(ex.Message);
            }
            catch (RequestFailedException ex)
            {
                AuthenticationService.Instance.ClearSessionToken();
                hasUnityIdentifier = false;
                doneUnityAuthentication = true;
                _onErrorPopup(ex.Message);
            }
        }

        public string googlePlayGamesToken = string.Empty;
        public string googlePlayGamesError = string.Empty;

        public void InitGooglePlayService()
        {
#if UNITY_ANDROID
            PlayGamesPlatform.Activate();
#endif
        }

        public string iOSToken = string.Empty;
        IAppleAuthManager _appleAuthManager;

        private void InitAppleSignIn()
        {
            var deserializer = new PayloadDeserializer();
            _appleAuthManager = new AppleAuthManager(deserializer);
        }

        public void LoginApple()
        {
            if (_appleAuthManager == null)
            {
                InitAppleSignIn();
            }
            
            Debug.Assert(_onErrorPopup != null, "_onErrorPopup != null");

            // Set the login arguments
            AppleAuthLoginArgs loginArgs = new(LoginOptions.IncludeEmail | LoginOptions.IncludeFullName);
            // Perform the login
            _appleAuthManager?.LoginWithAppleId(
                loginArgs,
                credential =>
                {
                    var appleIDCredential = credential as IAppleIDCredential;
                    if (appleIDCredential != null)
                    {
                        var idToken = Encoding.UTF8.GetString(
                            appleIDCredential.IdentityToken,
                            0,
                            appleIDCredential.IdentityToken.Length);
                        Debug.Log("Sign-in with Apple successfully done. IDToken: " + idToken);
                        iOSToken = idToken;
                        LinkWithAppleAsync(iOSToken);
                    }
                    else
                    {
                        Debug.Log("Sign-in with Apple error. Message: appleIDCredential is null");
                        _onErrorPopup("Sign-in with Apple error. Message: appleIDCredential is null");
                    }
                },
                error =>
                {
                    Debug.Log("Sign-in with Apple error. Message: " + error);
                    _onErrorPopup("Sign-in with Apple error. Message: " + error);
                }
            );
        } // ? end of apple login

        async Task LinkWithAppleAsync(string idToken)
        {
            try
            {
                await AuthenticationService.Instance.LinkWithAppleAsync(idToken);
                Debug.Log("Link is successful.");

                await GetPlayerInfoAsync();
                _onRefresh();
                // PlayerExternalIds += "apple.com";
                // PopupAccount.RefreshPopupAccount?.Invoke();
            }
            catch (AuthenticationException ex) when (ex.ErrorCode == AuthenticationErrorCodes.AccountAlreadyLinked)
            {
                // Prompt the player with an error message.
                Debug.LogError("This user is already linked with another account. Log in instead.");
                _onConfirmPopup(ChangeUnityGameServiceAccountAndApple);
                // SystemManager.CloseCurrentPopup();
                // SystemManager.ShowConfirmPopup(SystemManager.GetLocalizedText("6111"),
                //     ChangeUnityGameServiceAccountAndApple, SystemManager.ShowAccountLinkCancelAlert);
            }
            catch (AuthenticationException ex)
            {
                // Compare error code to AuthenticationErrorCodes
                // Notify the player with the proper error message
                Debug.LogException(ex);
                _onErrorPopup(ex.Message);
                // SystemManager.ShowSimpleAlert(ex.Message);
                // NetworkLoader.main.ReportRequestError(ex.Message, "AuthenticationException_link");
            }
            catch (RequestFailedException ex)
            {
                // Compare error code to CommonErrorCodes
                // Notify the player with the proper error message
                Debug.LogException(ex);
                _onErrorPopup(ex.Message);
                // SystemManager.ShowSimpleAlert(ex.Message);
                // NetworkLoader.main.ReportRequestError(ex.Message, "RequestFailedException_link");
            }
        }

        void Update()
        {
#if UNITY_IOS
            m_AppleAuthManager?.Update();
#endif
        }


        /// <summary>
        /// 구글 플레이 게임서비스 로그인
        /// </summary>
        public void LoginGoogle()
        {
            // Social.localUser.Authenticate(OnGoogleLogin);
#if UNITY_ANDROID
            // Debug.Log("### UGS LoginGooglePlayGames ");
            PlayGamesPlatform.Instance.Authenticate((success) =>
            {
                if (success == SignInStatus.Success)
                {
                    Debug.Log("UGS Login with Google Play games successful.");

                    PlayGamesPlatform.Instance.RequestServerSideAccess(true, code =>
                    {
                        Debug.Log("GPGS Authorization code: " + code);
                        googlePlayGamesToken = code;

                        // This token serves as an example to be used for SignInWithGooglePlayGames

                        LinkWithGooglePlayGamesAsync(googlePlayGamesToken);
                    });
                }
                else
                {
                    googlePlayGamesError = "Failed to retrieve Google play games authorization code ";
                    Debug.Log("UGS Login Unsuccessful");
                    
                    _onErrorPopup("Failed GooglePlay Login");
                }
            });
#endif
        }

        void OnGoogleLogin(bool success, string message)
        {
            if (success)
            {
                Debug.Log("Login with Google done. IdToken: " + Social.localUser.id);
            }
            else
            {
                googlePlayGamesError = "Failed to retrieve Google authorization code [" + message + "]";
                _onErrorPopup(googlePlayGamesError);
                Debug.Log("UGS Login Unsuccessful");
            }
        }


        /// <summary>
        /// 현재 계정에 구글플레이게임 연결하기 
        /// </summary>
        /// <param name="authCode"></param>
        /// <returns></returns>
        async public Task LinkWithGooglePlayGamesAsync(string authCode)
        {
            try
            {
                await AuthenticationService.Instance.LinkWithGooglePlayGamesAsync(authCode);
                await GetPlayerInfoAsync();
                Debug.Log("Link is successful.");

                // 정상적으로 연결됨
                // PopupAccount 리프레시 
                _onRefresh();
                // PopupAccount.RefreshPopupAccount?.Invoke();
            }
            catch (AuthenticationException ex) when (ex.ErrorCode == AuthenticationErrorCodes.AccountAlreadyLinked)
            {
                // Prompt the player with an error message.
                Debug.LogError("UGS This user is already linked with another account. Log in instead.");

                
                // 연결된 계정이 있으면 알려줘야한다. 
                // 현재 계정 모달을 닫고 컨펌 모달을 연다. 
                // SystemManager.CloseCurrentPopup();


                _onConfirmPopup(ChangeUnityGameServiceAccountAndGooglePlay);
                // SystemManager.ShowConfirmPopup(SystemManager.GetLocalizedText("6111"),
                    // ChangeUnityGameServiceAccountAndGooglePlay, SystemManager.ShowAccountLinkCancelAlert);
            }

            catch (AuthenticationException ex)
            {
                // Compare error code to AuthenticationErrorCodes
                // Notify the player with the proper error message
                Debug.LogException(ex);
                _onErrorPopup(ex.Message);
                // SystemManager.ShowSimpleAlert(ex.Message);
                // NetworkLoader.main.ReportRequestError(ex.Message, "AuthenticationException_link");
            }
            catch (RequestFailedException ex)
            {
                // Compare error code to CommonErrorCodes
                // Notify the player with the proper error message
                Debug.LogException(ex);
                _onErrorPopup(ex.Message);
                // SystemManager.ShowSimpleAlert(ex.Message);
                // NetworkLoader.main.ReportRequestError(ex.Message, "RequestFailedException_link");
            }
        }


        /// <summary>
        /// 로그아웃하고 전환한다.
        /// </summary>
        void ChangeUnityGameServiceAccountAndGooglePlay()
        {
#if UNITY_ANDROID
            Debug.Log("### ChangeUnityGameServiceAccountAndGooglePlay");

            AuthenticationService.Instance.SignOut();
            AuthenticationService.Instance.ClearSessionToken();


            PlayGamesPlatform.Instance.RequestServerSideAccess(true, code =>
            {
                Debug.Log("Authorization code: " + code);
                googlePlayGamesToken = code;

                // This token serves as an example to be used for SignInWithGooglePlayGames
                // 직접 로그인 시작     
                SignInWithGooglePlayGamesAsync(googlePlayGamesToken);
            });
#endif
        }

        void ChangeUnityGameServiceAccountAndApple()
        {
            Debug.Log("### ChangeUnityGameServiceAccountAndGooglePlay");

            AuthenticationService.Instance.SignOut();
            AuthenticationService.Instance.ClearSessionToken();


            SignInWithAppleAsync(iOSToken);
        }


        /// <summary>
        /// 연결된 계정이 있어서 구글플레이로 로그인 다시 하기 
        /// </summary>
        /// <param name="authCode"></param>
        /// <returns></returns>
        async public Task SignInWithGooglePlayGamesAsync(string authCode)
        {
            Debug.Log("UGS SignInWithGooglePlayGamesAsync Start");

            try
            {
                await AuthenticationService.Instance.SignInWithGooglePlayGamesAsync(authCode);
                Debug.Log("UGS Google Play SignIn is successful.");

                // Shows how to get the playerID
                Debug.Log($"Changed UGS PlayerID: {AuthenticationService.Instance.PlayerId}");
                hasUnityIdentifier = true;
                doneUnityAuthentication = true;

                await GetPlayerInfoAsync();

                // 성공했으면 씬 다시 로드시키면서 정보 리프레시
                // SystemManager.main.ChangeAccount();
                _onRefresh();
            }
            catch (AuthenticationException ex)
            {
                // Compare error code to AuthenticationErrorCodes
                // Notify the player with the proper error message
                Debug.LogException(ex);
                _onErrorPopup(ex.Message);
                // SystemManager.ShowSimpleAlert(ex.Message);
                // NetworkLoader.main.ReportRequestError(ex.Message, "AuthenticationException_google");
            }
            catch (RequestFailedException ex)
            {
                // Compare error code to CommonErrorCodes
                // Notify the player with the proper error message
                Debug.LogException(ex);
                _onErrorPopup(ex.Message);
                // SystemManager.ShowSimpleAlert(ex.Message);
                // NetworkLoader.main.ReportRequestError(ex.Message, "RequestFailedException_google");
            }
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="idToken"></param>
        /// <returns></returns>
        private async Task SignInWithAppleAsync(string idToken)
        {
            try
            {
                await AuthenticationService.Instance.SignInWithAppleAsync(idToken);
                Debug.Log("UGS Apple SignIn is successful.");

                Debug.Log($"Changed UGS PlayerID: {AuthenticationService.Instance.PlayerId}");
                hasUnityIdentifier = true;
                doneUnityAuthentication = true;

                await GetPlayerInfoAsync();

                // 성공했으면 씬 다시 로드시키면서 정보 리프레시
                _onRefresh();
                
                // SystemManager.main.ChangeAccount();
            }
            catch (AuthenticationException ex)
            {
                // Compare error code to AuthenticationErrorCodes
                // Notify the player with the proper error message
                Debug.LogException(ex);
                _onErrorPopup(ex.Message);

                // SystemManager.ShowSimpleAlert(ex.Message);
                // NetworkLoader.main.ReportRequestError(ex.Message, "AuthenticationException_apple");
            }
            catch (RequestFailedException ex)
            {
                // Compare error code to CommonErrorCodes
                // Notify the player with the proper error message
                Debug.LogException(ex);
                _onErrorPopup(ex.Message);
                // SystemManager.ShowSimpleAlert(ex.Message);
                // NetworkLoader.main.ReportRequestError(ex.Message, "RequestFailedException_apple");
            }
        }

        private string GetExternalIds(PlayerInfo playerInfo)
        {
            StringBuilder sb = new();
            if (playerInfo.Identities == null)
            {
                return string.Empty;
            }

            foreach (var id in playerInfo.Identities)
                sb.Append(id.TypeId + " ");

            return sb.ToString();

        }

        private async Task GetPlayerInfoAsync()
        {
            PlayerInfo playerInfo = await AuthenticationService.Instance.GetPlayerInfoAsync();
            playerExternalIds = GetExternalIds(playerInfo);
        }
    }
}