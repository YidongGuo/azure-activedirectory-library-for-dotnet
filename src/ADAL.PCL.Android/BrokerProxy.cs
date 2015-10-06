//----------------------------------------------------------------------
// Copyright (c) Microsoft Open Technologies, Inc.
// All Rights Reserved
// Apache License 2.0
//
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
// 
// http://www.apache.org/licenses/LICENSE-2.0
// 
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.
//----------------------------------------------------------------------

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Android.Accounts;
using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Android.Widget;
using Android.Content.PM;
using Android.Util;
using Java.Security;
using Java.Util.Concurrent;
using OperationCanceledException = Android.Accounts.OperationCanceledException;
using Permission = Android.Content.PM.Permission;
using Signature = Android.Content.PM.Signature;

namespace Microsoft.IdentityModel.Clients.ActiveDirectory
{

    class BrokerProxy
    {
        private const string RedirectUriScheme = "msauth";
        private Context mContext;
        private AccountManager mAcctManager;
        private string mBrokerTag;
        public const string DATA_USER_INFO = "com.microsoft.workaccount.user.info";
        

        public BrokerProxy()
        {
            mBrokerTag = BrokerConstants.Signature;
        }

        public BrokerProxy(Context ctx)
        {
            mContext = ctx;
            mAcctManager = AccountManager.Get(mContext);
            mBrokerTag = BrokerConstants.Signature;
        }

        public bool CanSwitchToBroker()
        {
            string packageName = mContext.PackageName;

            // ADAL switches broker for following conditions:
            // 1- app is not skipping the broker
            // 2- permissions are set in the manifest,
            // 3- if package is not broker itself for both company portal and azure
            // authenticator
            // 4- signature of the broker is valid
            // 5- account exists
            return  VerifyManifestPermissions()
                    && CheckAccount(mAcctManager, "", "")
                    && !packageName.Equals(BrokerConstants.PackageName, StringComparison.OrdinalIgnoreCase)
                    && !packageName
                            .Equals(BrokerConstants.AzureAuthenticatorAppPackageName, StringComparison.OrdinalIgnoreCase)
                    && VerifyAuthenticator(mAcctManager);
        }
        
        public bool VerifyUser(String username, String uniqueid)
        {
            return CheckAccount(mAcctManager, username, uniqueid);
        }
        
        public bool CanUseLocalCache()
        {
            bool brokerSwitch = CanSwitchToBroker();
            if (!brokerSwitch)
            {
                PlatformPlugin.Logger.Verbose(null, "It does not use broker");
                return true;
            }

            string packageName = mContext.PackageName;
            if (VerifySignature(packageName))
            {
                PlatformPlugin.Logger.Verbose(null, "Broker installer can use local cache");
                return true;
            }

            return false;
        }

        // App needs to give permission to AccountManager to use broker.
        private bool VerifyManifestPermissions()
        {
            return VerifyManifestPermission("android.permission.GET_ACCOUNTS") &&
            VerifyManifestPermission("android.permission.MANAGE_ACCOUNTS") &&
            VerifyManifestPermission("android.permission.USE_CREDENTIALS");
        }

        private bool VerifyManifestPermission(string permission)
        {
            if (Permission.Granted != Application.Context.PackageManager.CheckPermission(permission, Application.Context.PackageName))
            {
                PlatformPlugin.Logger.Information(null, String.Format(AdalErrorMessageEx.MissingPackagePermissionTemplate, permission));
                return false;
            }

            return true;
        }

        private void VerifyNotOnMainThread()
        {
            Looper looper = Looper.MyLooper();
            if (looper != null && looper == mContext.MainLooper)
            {
                Exception exception = new Exception(
                        "calling this from your main thread can lead to deadlock");
                PlatformPlugin.Logger.Error(null, exception);
                if (mContext.ApplicationInfo.TargetSdkVersion >= Build.VERSION_CODES.Froyo)
                {
                    throw exception;
                }
            }
        }

        private Account FindAccount(String accountName, Account[] accountList)
        {
            if (accountList != null)
            {
                foreach (Account account in accountList)
                {
                    if (account != null && account.Name != null
                            && account.Name.Equals(accountName,StringComparison.OrdinalIgnoreCase))
                    {
                        return account;
                    }
                }
            }

            return null;
        }

        private UserInfo FindUserInfo(String userid, UserInfo[] userList)
        {
            if (userList != null)
            {
                foreach (UserInfo user in userList)
                {
                    if (user != null && !String.IsNullOrEmpty(user.UniqueId)
                            && user.UniqueId.Equals(userid, StringComparison.OrdinalIgnoreCase))
                    {
                        return user;
                    }
                }
            }

            return null;
        }

        public AuthenticationResultEx GetAuthTokenInBackground(AuthenticationRequest request, Activity callerActivity)
        {

            AuthenticationResultEx authResult = null;
            VerifyNotOnMainThread();

            // if there is not any user added to account, it returns empty
            Account targetAccount = null;
            Account[] accountList = mAcctManager
                    .GetAccountsByType(BrokerConstants.BROKER_ACCOUNT_TYPE);

            if (!String.IsNullOrEmpty(request.BrokerAccountName))
            {
                targetAccount = FindAccount(request.BrokerAccountName, accountList);
            }
            else
            {
                try
                {
                    UserInfo[] users = GetBrokerUsers();
                    UserInfo matchingUser = FindUserInfo(request.UserId, users);
                    if (matchingUser != null)
                    {
                        targetAccount = FindAccount(matchingUser.DisplayableId, accountList);
                    }
                }
                catch (Exception e)
                {
                    PlatformPlugin.Logger.Error(null, e);
                }
            }

            if (targetAccount != null)
            {
                Bundle brokerOptions = GetBrokerOptions(request);

                // blocking call to get token from cache or refresh request in
                // background at Authenticator
                IAccountManagerFuture result = null;
                try
                {
                    // It does not expect activity to be launched.
                    // AuthenticatorService is handling the request at
                    // AccountManager.
                    //
                    result = mAcctManager.GetAuthToken(targetAccount,
                            BrokerConstants.AUTHTOKEN_TYPE, brokerOptions, false,
                            null /*
                              * set to null to avoid callback
                              */, new Handler(callerActivity.MainLooper));

                    // Making blocking request here
                    PlatformPlugin.Logger.Verbose(null, "Received result from Authenticator");
                    Bundle bundleResult = (Bundle)result.GetResult(10000, TimeUnit.Milliseconds);
                    // Authenticator should throw OperationCanceledException if
                    // token is not available
                    authResult = GetResultFromBrokerResponse(bundleResult);
                }
                catch (OperationCanceledException e)
                {
                    PlatformPlugin.Logger.Error(null, e);
                }
                catch (AuthenticatorException e)
                {
                    PlatformPlugin.Logger.Error(null, e);
                }
                catch (Exception e)
                {
                    // Authenticator gets problem from webrequest or file read/write
                    /*                    Logger.e(TAG, "Authenticator cancels the request", "",
                                                ADALError.BROKER_AUTHENTICATOR_IO_EXCEPTION);*/

                    PlatformPlugin.Logger.Error(null, e);
                }

                PlatformPlugin.Logger.Verbose(null, "Returning result from Authenticator");
                return authResult;
            }
            else
            {
                PlatformPlugin.Logger.Verbose(null, "Target account is not found");
            }

            return null;
        }

        private AuthenticationResultEx GetResultFromBrokerResponse(Bundle bundleResult)
        {
            if (bundleResult == null)
            {
                throw new Exception("bundleResult");
            }

            int errCode = bundleResult.GetInt(AccountManager.KeyErrorCode);
            String msg = bundleResult.GetString(AccountManager.KeyErrorMessage);
            if (!String.IsNullOrEmpty(msg))
            {
                throw new AdalException(errCode.ToString(), msg);
            }
            else
            {
                bool initialRequest = bundleResult.ContainsKey(BrokerConstants.ACCOUNT_INITIAL_REQUEST);
                if (initialRequest)
                {
                    // Initial request from app to Authenticator needs to launch
                    // prompt. null resultEx means initial request
                    return null;
                }

                // IDtoken is not present in the current broker user model
                UserInfo userinfo = GetUserInfoFromBrokerResult(bundleResult);
                return new AuthenticationResultEx
                {
                    RefreshToken = null,
                    ResourceInResponse = null,
                    Result = new AuthenticationResult("Bearer", bundleResult.GetString(AccountManager.KeyAuthtoken), ConvertFromTimeT(bundleResult.GetLong("account.expiredate", 0)))
                    {
                      UserInfo = userinfo
                    }
                };
            }
        }

        internal static DateTimeOffset ConvertFromTimeT(long seconds)
        {
            var startTime = new DateTimeOffset(1970, 1, 1, 0, 0, 0, 0, TimeSpan.Zero);
            return startTime.AddMilliseconds(seconds);
        }


        internal static UserInfo GetUserInfoFromBrokerResult(Bundle bundle)
        {
            // Broker has one user and related to ADFS WPJ user. It does not return
            // idtoken
            String userid = bundle.GetString(BrokerConstants.ACCOUNT_USERINFO_USERID);
            String givenName = bundle
                    .GetString(BrokerConstants.ACCOUNT_USERINFO_GIVEN_NAME);
            String familyName = bundle
                    .GetString(BrokerConstants.ACCOUNT_USERINFO_FAMILY_NAME);
            String identityProvider = bundle
                    .GetString(BrokerConstants.ACCOUNT_USERINFO_IDENTITY_PROVIDER);
            String displayableId = bundle
                    .GetString(BrokerConstants.ACCOUNT_USERINFO_USERID_DISPLAYABLE);
            return new UserInfo
            {
                UniqueId = userid,
                GivenName = givenName,
                FamilyName = familyName,
                IdentityProvider = identityProvider,
                DisplayableId = displayableId
            };
        }

        public Intent GetIntentForBrokerActivity(AuthenticationRequest request, Activity callerActivity)
        {
            Intent intent = null;
            IAccountManagerFuture result = null;
            try
            {
                // Callback is not passed since it is making a blocking call to get
                // intent. Activity needs to be launched from calling app
                // to get the calling app's metadata if needed at BrokerActivity.
                Bundle addAccountOptions = GetBrokerOptions(request);
                result = mAcctManager.AddAccount(BrokerConstants.BROKER_ACCOUNT_TYPE,
                        BrokerConstants.AUTHTOKEN_TYPE, null, addAccountOptions, null,
                        null, new Handler(callerActivity.MainLooper));

                // Making blocking request here
                Bundle bundleResult = (Bundle)result.Result;
                // Authenticator should throw OperationCanceledException if
                // token is not available
                intent = (Intent)bundleResult.GetParcelable(AccountManager.KeyIntent);

                // Add flag to this intent to signal that request is for broker
                // logic
                if (intent != null)
                {
                    intent.PutExtra(BrokerConstants.BROKER_REQUEST, BrokerConstants.BROKER_REQUEST);
                }
            }
            catch (OperationCanceledException e)
            {
                PlatformPlugin.Logger.Error(null, e);
            }
            catch (Exception e)
            {
                // Authenticator gets problem from webrequest or file read/write
                PlatformPlugin.Logger.Error(null, new AdalException("Authenticator cancels the request", e));
            }

            return intent;
        }

        private string GetRedirectUriForBroker()
        {
            string packageName = Application.Context.PackageName;

            // First available signature. Applications can be signed with multiple
            // signatures.
            string signatureDigest = this.GetCurrentSignatureForPackage(packageName);
            if (!String.IsNullOrEmpty(signatureDigest))
            {
                return String.Format("{0}://{1}/{2}", RedirectUriScheme, EncodingHelper.UrlEncode(packageName), EncodingHelper.UrlEncode(signatureDigest));
            }

            return String.Empty;
        }

        private string GetCurrentSignatureForPackage(string packageName)
        {
            try
            {
                PackageInfo info = Application.Context.PackageManager.GetPackageInfo(packageName, PackageInfoFlags.Signatures);
                if (info != null && info.Signatures != null && info.Signatures.Count > 0)
                {
                    Signature signature = info.Signatures[0];
                    MessageDigest md = MessageDigest.GetInstance("SHA");
                    md.Update(signature.ToByteArray());
                    return Convert.ToBase64String(md.Digest(), Base64FormattingOptions.None);
                    // Server side needs to register all other tags. ADAL will
                    // send one of them.
                }
            }
            catch (PackageManager.NameNotFoundException)
            {
                PlatformPlugin.Logger.Information(null, "Calling App's package does not exist in PackageManager");
            }
            catch (NoSuchAlgorithmException)
            {
                PlatformPlugin.Logger.Information(null, "Digest SHA algorithm does not exists");
            }

            return null;
        }

        private Bundle GetBrokerOptions(AuthenticationRequest request)
        {
            Bundle brokerOptions = new Bundle();
            // request needs to be parcelable to send across process
            brokerOptions.PutInt("com.microsoft.aad.adal:RequestId", request.RequestId);
            brokerOptions.PutString(BrokerConstants.ACCOUNT_AUTHORITY,
                    request.Authority);
            brokerOptions.PutInt("json", 1);
            brokerOptions.PutString(BrokerConstants.ACCOUNT_RESOURCE,
                    request.Resource);
            string s = GetRedirectUriForBroker();
            brokerOptions.PutString(BrokerConstants.ACCOUNT_REDIRECT, s);
            brokerOptions.PutString(BrokerConstants.ACCOUNT_CLIENTID_KEY,
                    request.ClientId);
            brokerOptions.PutString(BrokerConstants.ADAL_VERSION_KEY,
                    request.Version);
            brokerOptions.PutString(BrokerConstants.ACCOUNT_EXTRA_QUERY_PARAM,
                    request.ExtraQueryParamsAuthentication);
            if (request.CorrelationId != null)
            {
                brokerOptions.PutString(BrokerConstants.ACCOUNT_CORRELATIONID, request
                        .CorrelationId.ToString());
            }

            String username = request.BrokerAccountName;
            if (String.IsNullOrEmpty(username))
            {
                username = request.LoginHint;
            }

            brokerOptions.PutString(BrokerConstants.ACCOUNT_LOGIN_HINT, username);
            brokerOptions.PutString(BrokerConstants.ACCOUNT_NAME, username);

            return brokerOptions;
        }

        private string GetCurrentUser()
        {
            // authenticator is not used if there is not any user
            Account[] accountList = mAcctManager.GetAccountsByType(BrokerConstants.BROKER_ACCOUNT_TYPE);
            return (accountList != null && accountList.Length > 0) ? accountList[0].Name : null;
        }

        private bool CheckAccount(AccountManager am, String username, String uniqueId)
        {
            AuthenticatorDescription[] authenticators = am.GetAuthenticatorTypes();
            foreach (AuthenticatorDescription authenticator in authenticators)
            {
                if (authenticator.Type.Equals(BrokerConstants.BROKER_ACCOUNT_TYPE))
                {

                    Account[] accountList = mAcctManager
                            .GetAccountsByType(BrokerConstants.BROKER_ACCOUNT_TYPE);

                    // Authenticator installed from Company portal
                    // This supports only one account
                    if (authenticator.PackageName
                            .Equals(BrokerConstants.PackageName, StringComparison.OrdinalIgnoreCase))
                    {
                        // Adal should not connect if given username does not match
                        if (accountList != null && accountList.Length > 0)
                        {
                            return VerifyAccount(accountList, username, uniqueId);
                        }

                        return false;

                        // Check azure authenticator and allow calls for test
                        // versions
                    }
                    else if (authenticator.PackageName
                          .Equals(BrokerConstants.AzureAuthenticatorAppPackageName, StringComparison.OrdinalIgnoreCase)
                          || authenticator.PackageName
                                  .Equals(BrokerConstants.PackageName, StringComparison.OrdinalIgnoreCase))
                    {

                        // Existing broker logic only connects to broker for token
                        // requests if account exists. New version can allow to
                        // add accounts through Adal.
                        if (HasSupportToAddUserThroughBroker())
                        {
                            PlatformPlugin.Logger.Verbose(null, "Broker supports to add user through app");
                            return true;
                        }
                        else if (accountList != null && accountList.Length > 0)
                        {
                            return VerifyAccount(accountList, username, uniqueId);
                        }
                    }
                }
            }

            return false;
        }

        private bool VerifyAccount(Account[] accountList, String username, String uniqueId)
        {
            if (!String.IsNullOrEmpty(username))
            {
                return username.Equals(accountList[0].Name, StringComparison.OrdinalIgnoreCase);
            }

            if (!String.IsNullOrEmpty(uniqueId))
            {
                // Uniqueid for account at authenticator is not available with
                // Account
                UserInfo[] users;
                try
                {
                    users = GetBrokerUsers();
                    UserInfo matchingUser = FindUserInfo(uniqueId, users);
                    return matchingUser != null;
                }
                catch (Exception e)
                {
                    PlatformPlugin.Logger.Error(null, e);
                }

                PlatformPlugin.Logger.Verbose(null, "It could not check the uniqueid from broker. It is not using broker");
                return false;
            }

            // if username or uniqueid not specified, it should use the broker
            // account.
            return true;
        }

        private bool HasSupportToAddUserThroughBroker()
        {
                        Intent intent = new Intent();
                        intent.SetPackage(BrokerConstants.AzureAuthenticatorAppPackageName);
                        intent.SetClassName(BrokerConstants.AzureAuthenticatorAppPackageName,
                                BrokerConstants.AzureAuthenticatorAppPackageName
                                        + ".ui.AccountChooserActivity");
                        PackageManager packageManager = mContext.PackageManager;
                        IList<ResolveInfo> infos = packageManager.QueryIntentActivities(intent, 0);
                        return infos.Count > 0;
            
        }

        private bool VerifySignature(string brokerPackageName)
        {
            try
            {
                PackageInfo info = Application.Context.PackageManager.GetPackageInfo(brokerPackageName, PackageInfoFlags.Signatures);
                if (info == null || info.Signatures == null)
                {
                    PlatformPlugin.Logger.Information(null, AdalErrorMessageEx.FailedToGetBrokerAppSignature);
                    return false;
                }

                // Broker App can be signed with multiple certificates. It will
                // look all of them until it finds the correct one for ADAL
                // broker.

                foreach (var signature in info.Signatures)
                {
                    MessageDigest md = MessageDigest.GetInstance("SHA");
                    md.Update(signature.ToByteArray());
                    string tag = Base64.EncodeToString(md.Digest(), Base64Flags.NoWrap);

                    // Company portal(Intune) app and Azure authenticator app
                    // have authenticator.
                    if (tag == mBrokerTag || tag == BrokerConstants.AzureAuthenticatorAppSignature)
                    {
                        return true;
                    }
                }

                PlatformPlugin.Logger.Information(null, AdalErrorMessageEx.IncorrectBrokerAppSignate);
                return false;
            }
            catch (PackageManager.NameNotFoundException ex)
            {
                throw new AdalException(AdalErrorEx.MissingBrokerRelatedPackage, AdalErrorMessageEx.MissingBrokerRelatedPackage, ex);
            }
            catch (NoSuchAlgorithmException ex)
            {
                throw new AdalException(AdalErrorEx.MissingDigestShaAlgorithm, AdalErrorMessageEx.MissingDigestShaAlgorithm, ex);
            }
            catch (Exception ex)
            {
                throw new AdalException(AdalErrorEx.SignatureVerificationFailed, AdalErrorMessageEx.SignatureVerificationFailed, ex);
            }
        }


        private bool VerifyAuthenticator(AccountManager am)
        {
            // there may be multiple authenticators from same package
            // , but there is only one entry for an authenticator type in
            // AccountManager.
            // If another app tries to install same authenticator type, it will
            // queue up and will be active after first one is uninstalled.
            AuthenticatorDescription[] authenticators = am.GetAuthenticatorTypes();
            foreach (AuthenticatorDescription authenticator in authenticators)
            {
                if (authenticator.Type.Equals(BrokerConstants.BROKER_ACCOUNT_TYPE)
                        && VerifySignature(authenticator.PackageName))
                {
                    return true;
                }
            }

            return false;
        }


    public UserInfo[] GetBrokerUsers() {

        // Calling this on main thread will cause exception since this is
        // waiting on AccountManagerFuture
        if (Looper.MyLooper() == Looper.MainLooper) {
            throw new Exception("Calling getBrokerUsers on main thread");
        }

    Account[] accountList = mAcctManager
            .GetAccountsByType(BrokerConstants.BROKER_ACCOUNT_TYPE);
    Bundle bundle = new Bundle();
    bundle.PutBoolean(DATA_USER_INFO, true);

        if (accountList != null) {

            // get info for each user
            UserInfo[] users = new UserInfo[accountList.Length];
            for (int i = 0; i<accountList.Length; i++) {

                // Use AccountManager Api method to get extended user info
                IAccountManagerFuture result = mAcctManager.UpdateCredentials(
                        accountList[i], BrokerConstants.AUTHTOKEN_TYPE, bundle,
                        null, null, null);
    PlatformPlugin.Logger.Verbose(null, "Waiting for the result");
                Bundle userInfoBundle = (Bundle)result.Result;

                users[i] = new UserInfo
                {
                    UniqueId = userInfoBundle
                    .GetString(BrokerConstants.ACCOUNT_USERINFO_USERID),
                    GivenName = userInfoBundle
                                .GetString(BrokerConstants.ACCOUNT_USERINFO_GIVEN_NAME),
                    FamilyName = userInfoBundle
                                .GetString(BrokerConstants.ACCOUNT_USERINFO_FAMILY_NAME),
                    IdentityProvider = userInfoBundle
                                .GetString(BrokerConstants.ACCOUNT_USERINFO_IDENTITY_PROVIDER),
                    DisplayableId = userInfoBundle
                                .GetString(BrokerConstants.ACCOUNT_USERINFO_USERID_DISPLAYABLE)
            };
            
            }

            return users;
        }
        return null;
    }
    }
}