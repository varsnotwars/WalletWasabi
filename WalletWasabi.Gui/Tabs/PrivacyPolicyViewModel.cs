using System;
using System.IO;
using Avalonia;
using Avalonia.Platform;
using WalletWasabi.Gui.ViewModels;

namespace WalletWasabi.Gui.Tabs
{
	internal class PrivacyPolicyViewModel : WasabiDocumentTabViewModel
	{
		private static string PrivacyPolicyText;

		public PrivacyPolicyViewModel(Global global) : base(global, "Privacy Policy")
		{
			if (PrivacyPolicyText == null)
			{
				var target = new Uri("resm:WalletWasabi.Gui.Assets.PrivacyPolicy.txt");
				var assetLocator = AvaloniaLocator.Current.GetService<IAssetLoader>();

				using (var stream = assetLocator.Open(target))
				using (var reader = new StreamReader(stream))
				{
					PrivacyPolicyText = reader.ReadToEnd();
				}
			}
		}

		public string PrivacyPolicy => PrivacyPolicyText;
	}
}
