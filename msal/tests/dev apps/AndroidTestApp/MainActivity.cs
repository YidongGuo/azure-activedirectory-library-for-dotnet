//----------------------------------------------------------------------
//
// Copyright (c) Microsoft Corporation.
// All rights reserved.
//
// This code is licensed under the MIT License.
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files(the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and / or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions :
//
// The above copyright notice and this permission notice shall be included in
// all copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN
// THE SOFTWARE.
//
//------------------------------------------------------------------------------

using System;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using Android.Widget;
using Android.OS;
using Microsoft.Identity.Client;
using System.Collections.Generic;
using System.Linq;

namespace AndroidTestApp
{
    [Activity(Label = "AndroidTestApp", MainLauncher = true, Icon = "@drawable/icon")]
    public class MainActivity : Activity
    {
        public static PublicClientApplication MsalPublicClient;

        private const string clientId = "5a434691-ccb2-4fd1-b97b-b64bcfbc03fc";

        private const string redirectUri = "msauth-5a434691-ccb2-4fd1-b97b-b64bcfbc03fc://com.microsoft.identity.client.sample";

        public const string DefaultAuthority = "https://login.microsoftonline.com/common";

        public static string[] DefaultScopes = { "User.Read" };

        public static string Authority = DefaultAuthority;

        public static string[] Scopes = DefaultScopes;

        public static UIBehavior uiBehavior = UIBehavior.SelectAccount;

        private UITextView accessTokenTextView;

        public bool UseEmbeddedWebview { get; set; }

        protected override void OnCreate(Bundle bundle)
        {
            base.OnCreate(bundle);

            // Set our view from the "main" layout resource
            SetContentView(Resource.Layout.activity_main);

            MsalPublicClient = new PublicClientApplication(clientId, Authority);

            Button acquireTokenInteractiveButton = FindViewById<Button>(Resource.Id.acquireTokenInteractiveButton);
            acquireTokenInteractiveButton.Click += acquireTokenInteractiveButton_Click;

            Button acquireTokenSilentButton = FindViewById<Button>(Resource.Id.acquireTokenSilentButton);
            acquireTokenSilentButton.Click += acquireTokenSilentButton_Click;

            Button clearCacheButton = FindViewById<Button>(Resource.Id.clearCacheButton);
            clearCacheButton.Click += clearCacheButton_Click;

            Button useEmbeddedWebview = FindViewById<Button>(Resource.Id.useEmbeddedWebviewSwitch);
            useEmbeddedWebview.Click += useEmbeddedWebview_Click;

            Button isSystemBrowser = FindViewById<Button>(Resource.Id.isSystemBrowserButton);
            isSystemBrowser.Click += isSystemBrowser_Click;

            this.accessTokenTextView = new UITextView(this, FindViewById<TextView>(Resource.Id.accessTokenTextView));
        }

        protected override void OnActivityResult(int requestCode, Result resultCode, Intent data)
        {
            base.OnActivityResult(requestCode, resultCode, data);
            AuthenticationContinuationHelper.SetAuthenticationContinuationEventArgs(requestCode, resultCode, data);
        }

        private async void acquireTokenSilentButton_Click(object sender, EventArgs e)
        {
            this.accessTokenTextView.Text = string.Empty;
            IEnumerable<IAccount> accounts = await MsalPublicClient.GetAccountsAsync();
            IAccount firstAccount = accounts.FirstOrDefault();
            string value = null;
            try
            {
                AuthenticationResult result =
                   await MsalPublicClient.AcquireTokenSilentAsync(Scopes, firstAccount);
                value = firstAccount + "\nAccess Token:\n" + result.AccessToken;
            }
            catch (Java.Lang.Exception ex)
            {
                throw new Exception(ex.Message + "\n" + ex.StackTrace);
            }
            catch (Exception exc)
            {
                value = exc.Message;
            }

            this.accessTokenTextView.Text = value;
        }

        private async void acquireTokenInteractiveButton_Click(object sender, EventArgs e)
        {
            this.accessTokenTextView.Text = string.Empty;
            string value = null;
            try
            {
                AuthenticationResult result;
                MsalPublicClient.RedirectUri = redirectUri;
                result = await MsalPublicClient.AcquireTokenAsync(Scopes, new UIParent(this, UseEmbeddedWebview));
                IEnumerable<IAccount> accounts = await MsalPublicClient.GetAccountsAsync();
                IAccount firstAccount = accounts.FirstOrDefault();
                IAccount currentUser = await MsalPublicClient.GetAccountAsync(firstAccount.HomeAccountId.Identifier);
                value = firstAccount + "\nAccess Token:\n" + result.AccessToken;
            }
            catch (Java.Lang.Exception ex)
            {
                throw new Exception(ex.Message + "\n" + ex.StackTrace);
            }
            catch (Exception exc)
            {
                value = exc.Message;
            }

            this.accessTokenTextView.Text = value;
        }

        private void isSystemBrowser_Click(object sender, EventArgs e)
        {
            this.accessTokenTextView.Text = string.Empty;
            UseEmbeddedWebview = UIParent.IsSystemWebviewAvailable();
            if (!UseEmbeddedWebview)
            {
                // Chrome not present on device, use embedded webview
                UseEmbeddedWebview = true;
                this.accessTokenTextView.Text = "Chrome not present on device. Using embedded webview";
            }
            else
            {
                UseEmbeddedWebview = false;
                this.accessTokenTextView.Text = "Chrome present on device. Using system browser";
            }
        }

        private void useEmbeddedWebview_Click(object sender, EventArgs e)
        {
            UseEmbeddedWebview = true;
        }

        private async void clearCacheButton_Click(object sender, EventArgs e)
        {
            IEnumerable<IAccount> accounts = await MsalPublicClient.GetAccountsAsync();
            IAccount firstAccount = accounts.FirstOrDefault();
            IAccount currentUser = await MsalPublicClient.GetAccountAsync(firstAccount.HomeAccountId.Identifier);

            await MsalPublicClient.RemoveAsync(currentUser).ConfigureAwait(false);

            this.accessTokenTextView.Text = "Cache cleared";
        }
    }
}