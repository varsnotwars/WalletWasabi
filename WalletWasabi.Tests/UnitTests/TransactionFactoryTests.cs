using NBitcoin;
using System;
using System.Collections.Generic;
using System.Linq;
using WalletWasabi.Exceptions;
using WalletWasabi.KeyManagement;
using WalletWasabi.Models;
using WalletWasabi.Models.TransactionBuilding;
using WalletWasabi.Services;
using Xunit;

namespace WalletWasabi.Tests.UnitTests
{
	public class TransactionFactoryTests
	{
		[Fact]
		public void InsufficientBalance()
		{
			var transactionFactory = CreateTransactionFactory(new[]{
				("Martin", 0, 0.01m, confirmed: true, anonymitySet: 1),
				("Jean",  1, 0.02m, confirmed: true, anonymitySet: 1)
			});

			// We try to spend 100btc but we only have 0.03
			var amount = Money.Coins(100m);
			var payment = new PaymentIntent(new Key().ScriptPubKey, amount);

			var ex = Assert.Throws<InsufficientBalanceException>(() => transactionFactory.BuildTransaction(payment, new FeeRate(2m)));

			Assert.Equal(ex.Minimum, amount);
			Assert.Equal(ex.Actual, transactionFactory.Coins.Select(x => x.Amount).Sum());
		}

		[Fact]
		public void TooMuchFeePaid()
		{
			var transactionFactory = CreateTransactionFactory(new[]{
				("Pablo", 0, 0.0001m, confirmed: true, anonymitySet: 1),
			});

			var payment = new PaymentIntent(new Key().ScriptPubKey, MoneyRequest.CreateAllRemaining(subtractFee: true));

			var result = transactionFactory.BuildTransaction(payment, new FeeRate(44.25m));
			var output = Assert.Single(result.OuterWalletOutputs);
			Assert.Equal(result.Fee, output.Amount); // edge case! paid amout equal to paid fee

			// The transaction cost is higher than the intended payment.
			var ex = Assert.Throws<InvalidOperationException>(() => transactionFactory.BuildTransaction(payment, new FeeRate(50m)));
			Assert.StartsWith("The transaction fee is more than twice the transaction amount", ex.Message);
		}

		[Fact]
		public void SelectMostPrivateCoin()
		{
			var transactionFactory = CreateTransactionFactory(new[]{
				("Maria",  0, 0.08m, confirmed: true, anonymitySet:  50),
				("Joseph", 1, 0.16m, confirmed: true, anonymitySet: 200),
			});

			// There is a 0.8 coin with AS=50. However it selects the most private one with AS= 200
			var destination = new Key().ScriptPubKey;
			var payment = new PaymentIntent(destination, Money.Coins(0.07m), label: new SmartLabel("Sophie"));
			var feeRate = new FeeRate(2m);
			var result = transactionFactory.BuildTransaction(payment, feeRate);

			Assert.True(result.Signed);
			var spentCoin = Assert.Single(result.SpentCoins);
			Assert.Equal(Money.Coins(0.16m), spentCoin.Amount);
			Assert.Equal(200, spentCoin.AnonymitySet);
			Assert.False(result.SpendsUnconfirmed);
			var tx = result.Transaction.Transaction;
			Assert.Equal(2, tx.Outputs.Count());

			var changeCoin = Assert.Single(result.InnerWalletOutputs);
			Assert.True(changeCoin.HdPubKey.IsInternal);
			Assert.Equal("Sophie", changeCoin.Label.ToString());  // Shouldn't this say: "Sophie, Joseph"????
		}

		[Fact]
		public void SelectMostPrivateCoins()
		{
			var transactionFactory = CreateTransactionFactory(new[]{
				("Pablo",  0, 0.01m, confirmed: true, anonymitySet: 1),
				("Jean",   1, 0.02m, confirmed: true, anonymitySet: 1),
				("Daniel", 2, 0.04m, confirmed: true, anonymitySet: 100),
				("Maria",  3, 0.08m, confirmed: true, anonymitySet: 50),
				("Joseph", 4, 0.16m, confirmed: true, anonymitySet: 200),
			});

			// It has to select the most private coins regarless of the amounts
			var payment = new PaymentIntent(new Key().ScriptPubKey, Money.Coins(0.17m));
			var feeRate = new FeeRate(2m);
			var result = transactionFactory.BuildTransaction(payment, feeRate);

			Assert.True(result.Signed);
			Assert.Equal(2, result.SpentCoins.Count());
			var spentCoin200 = Assert.Single(result.SpentCoins, x => x.AnonymitySet == 200);
			var spentCoin100 = Assert.Single(result.SpentCoins, x => x.AnonymitySet == 100);

			Assert.Equal(Money.Coins(0.16m), spentCoin200.Amount);
			Assert.Equal(Money.Coins(0.04m), spentCoin100.Amount);
			Assert.Single(result.OuterWalletOutputs);
			Assert.False(result.SpendsUnconfirmed);

			var tx = result.Transaction.Transaction;
			Assert.Equal(2, tx.Outputs.Count());
		}

		[Fact]
		public void SelectSameScriptPubKeyCoins()
		{
			var transactionFactory = CreateTransactionFactory(new[]{
				("Pablo",  0, 0.01m, confirmed: false, anonymitySet: 10),
				("Daniel", 1, 0.02m, confirmed: false, anonymitySet: 1),
				("Daniel", 1, 0.04m, confirmed: true, anonymitySet: 1),
				("Maria",  2, 0.08m, confirmed: true, anonymitySet: 20),
			});

			// Selecting 0.08 + 0.04 should be enough but it has to select 0.02 too because it is the same address
			var payment = new PaymentIntent(new Key().ScriptPubKey, Money.Coins(0.1m), label: new SmartLabel("Sophie"));
			var feeRate = new FeeRate(2m);
			var result = transactionFactory.BuildTransaction(payment, feeRate);

			Assert.True(result.Signed);
			Assert.True(result.SpendsUnconfirmed);
			Assert.Equal(4, result.SpentCoins.Count());
			Assert.Equal(Money.Coins(0.15m), result.SpentCoins.Select(x => x.Amount).Sum());

			var changeCoin = Assert.Single(result.InnerWalletOutputs);
			Assert.Equal("Sophie", changeCoin.Label.ToString());  // Shouldn't this say: "Sophie, Maria, Daniel"????

			var tx = result.Transaction.Transaction;
			// it must select the unconfirm coin even when the anonymity set is lower
			Assert.True(result.SpendsUnconfirmed);
			Assert.Equal(2, tx.Outputs.Count());
		}

		[Fact]
		public void CustomChangeScript()
		{
			var transactionFactory = CreateTransactionFactory(new[]{
				("Maria",  0, 1m, confirmed: true, anonymitySet: 100),
			});

			var destination = new Key().ScriptPubKey;
			var changeDestination = new Key().ScriptPubKey;
			var payment = new PaymentIntent(new[]{
				new DestinationRequest(destination, Money.Coins(0.1m)),
				new DestinationRequest(changeDestination, MoneyRequest.CreateChange(subtractFee: true))
			});
			var feeRate = new FeeRate(2m);
			var result = transactionFactory.BuildTransaction(payment, feeRate);

			Assert.True(result.Signed);
			Assert.Single(result.SpentCoins);
			Assert.Equal(Money.Coins(1m), result.SpentCoins.Select(x => x.Amount).Sum());

			var tx = result.Transaction.Transaction;
			Assert.Equal(2, tx.Outputs.Count());

			var changeOutput = Assert.Single(tx.Outputs, x => x.ScriptPubKey == changeDestination);
			Assert.Null(transactionFactory.KeyManager.GetKeyForScriptPubKey(changeOutput.ScriptPubKey));
			Assert.Equal(Money.Coins(0.9m), changeOutput.Value + result.Fee);
		}

		[Fact]
		public void SubtractFeeFromSpecificOutput()
		{
			var transactionFactory = CreateTransactionFactory(new[]{
				("Maria",  0, 1m, confirmed: true, anonymitySet: 100),
			});

			var destination1 = new Key().ScriptPubKey;
			var destination2 = new Key().ScriptPubKey;
			var destination3 = new Key().ScriptPubKey;
			var payment = new PaymentIntent(new[]{
				new DestinationRequest(destination1, Money.Coins(0.3m)),
				new DestinationRequest(destination2, Money.Coins(0.3m), subtractFee: true),
				new DestinationRequest(destination3, Money.Coins(0.3m)),
			});
			var feeRate = new FeeRate(2m);
			var result = transactionFactory.BuildTransaction(payment, feeRate);

			Assert.True(result.Signed);
			var spentCoin = Assert.Single(result.SpentCoins);
			Assert.Equal(Money.Coins(1m), spentCoin.Amount);

			var tx = result.Transaction.Transaction;
			Assert.Equal(4, tx.Outputs.Count());

			var destination2Output = Assert.Single(tx.Outputs, x => x.ScriptPubKey == destination2);
			Assert.Equal(Money.Coins(0.3m), destination2Output.Value + result.Fee);

			var changeOutput = Assert.Single(tx.Outputs, x => transactionFactory.KeyManager.GetKeyForScriptPubKey(x.ScriptPubKey) != null);
			Assert.Equal(Money.Coins(0.1m), changeOutput.Value);
		}

		[Fact]
		public void SubtractFeeFromTooSmallOutput()
		{
			var transactionFactory = CreateTransactionFactory(new[]{
				("Maria",  0, 1m, confirmed: true, anonymitySet: 100),
			});

			var destination1 = new Key().ScriptPubKey;
			var destination2 = new Key().ScriptPubKey;
			var destination3 = new Key().ScriptPubKey;
			var payment = new PaymentIntent(new[]{
				new DestinationRequest(destination1, Money.Coins(0.3m)),
				new DestinationRequest(destination2, Money.Coins(0.00001m), subtractFee: true),
				new DestinationRequest(destination3, Money.Coins(0.3m)),
			});
			var feeRate = new FeeRate(20m);
			var ex = Assert.Throws<NBitcoin.NotEnoughFundsException>(() => transactionFactory.BuildTransaction(payment, feeRate));

			Assert.Equal(Money.Satoshis(3240), ex.Missing);
		}

		[Fact]
		public void MultiplePaymentsToSameAddress()
		{
			var transactionFactory = CreateTransactionFactory(new[]{
				("Maria",  0, 1m, confirmed: true, anonymitySet: 100),
			});

			var destination = new Key().ScriptPubKey;
			var payment = new PaymentIntent(new[]{
				new DestinationRequest(destination, Money.Coins(0.3m)),
				new DestinationRequest(destination, Money.Coins(0.3m), subtractFee: true),
				new DestinationRequest(destination, Money.Coins(0.3m)),
			});
			var feeRate = new FeeRate(2m);
			var result = transactionFactory.BuildTransaction(payment, feeRate);

			Assert.True(result.Signed);
			var spentCoin = Assert.Single(result.SpentCoins);
			Assert.Equal(Money.Coins(1m), spentCoin.Amount);

			var tx = result.Transaction.Transaction;
			Assert.Equal(2, tx.Outputs.Count());  // consolidates same address payment

			var destinationOutput = Assert.Single(result.OuterWalletOutputs);
			Assert.Equal(destination, destinationOutput.ScriptPubKey);
			Assert.Equal(Money.Coins(0.9m), destinationOutput.Amount + result.Fee);

			var changeOutput = Assert.Single(result.InnerWalletOutputs);
			Assert.Equal(Money.Coins(0.1m), changeOutput.Amount);
		}

		[Fact]
		public void SendAbsolutelyAllCoins()
		{
			var transactionFactory = CreateTransactionFactory(new[]{
				("Maria",  0, 0.5m, confirmed: false, anonymitySet: 1),
				("Joseph", 1, 0.4m, confirmed: true, anonymitySet: 10),
				("Eve",    2, 0.3m, confirmed: false, anonymitySet: 40),
				("Julio",  3, 0.2m, confirmed: true, anonymitySet: 100),
			});

			var destination = new Key().ScriptPubKey;
			var payment = new PaymentIntent(new[]{
				new DestinationRequest(destination, MoneyRequest.CreateAllRemaining(subtractFee:true))
			});
			var feeRate = new FeeRate(2m);
			var result = transactionFactory.BuildTransaction(payment, feeRate);

			Assert.True(result.Signed);
			Assert.Equal(Money.Coins(1.4m), result.SpentCoins.Select(x => x.Amount).Sum());

			var tx = result.Transaction.Transaction;
			Assert.Single(tx.Outputs);
		}

		[Fact]
		public void SpendOnlyAllowedCoins()
		{
			var transactionFactory = CreateTransactionFactory(new[]{
				("Pablo",  0, 0.01m, confirmed: false, anonymitySet: 50),
				("Daniel", 1, 0.02m, confirmed: false, anonymitySet: 1),
				("Suyin",  2, 0.04m, confirmed: true, anonymitySet: 1),
				("Maria",  3, 0.08m, confirmed: true, anonymitySet: 100),
			});

			var destination = new Key().ScriptPubKey;
			var payment = new PaymentIntent(new Key().ScriptPubKey, Money.Coins(0.095m));
			var feeRate = new FeeRate(2m);
			var coins = transactionFactory.Coins;
			var allowedCoins = new[] {
				coins.Single(x=>x.Label.ToString() == "Maria"),
				coins.Single(x=>x.Label.ToString() == "Suyin")
			}.ToArray();
			var result = transactionFactory.BuildTransaction(payment, feeRate, allowedCoins.Select(x => x.GetTxoRef()));

			Assert.True(result.Signed);
			Assert.Equal(Money.Coins(0.12m), result.SpentCoins.Select(x => x.Amount).Sum());
			Assert.Equal(2, result.SpentCoins.Count());
			Assert.Contains(allowedCoins[0], result.SpentCoins);
			Assert.Contains(allowedCoins[1], result.SpentCoins);
		}

		[Fact]
		public void SpendWholeAllowedCoins()
		{
			var transactionFactory = CreateTransactionFactory(new[]{
				("Pablo",  0, 0.01m, confirmed: false, anonymitySet: 50),
				("Daniel", 1, 0.02m, confirmed: false, anonymitySet: 1),
				("Suyin",  2, 0.04m, confirmed: true, anonymitySet: 1),
				("Maria",  3, 0.08m, confirmed: true, anonymitySet: 100),
			});

			var destination = new Key().ScriptPubKey;
			var payment = new PaymentIntent(destination, MoneyRequest.CreateAllRemaining(subtractFee: true));
			var feeRate = new FeeRate(2m);
			var coins = transactionFactory.Coins;
			var allowedCoins = new[] {
				coins.Single(x=>x.Label.ToString() == "Pablo"),
				coins.Single(x=>x.Label.ToString() == "Maria"),
				coins.Single(x=>x.Label.ToString() == "Suyin")
			}.ToArray();
			var result = transactionFactory.BuildTransaction(payment, feeRate, allowedCoins.Select(x => x.GetTxoRef()));

			Assert.True(result.Signed);
			Assert.Equal(Money.Coins(0.13m), result.SpentCoins.Select(x => x.Amount).Sum());
			Assert.Equal(3, result.SpentCoins.Count());
			Assert.Contains(allowedCoins[0], result.SpentCoins);
			Assert.Contains(allowedCoins[1], result.SpentCoins);

			var tx = result.Transaction.Transaction;
			Assert.Single(tx.Outputs);

			var destinationutput = Assert.Single(tx.Outputs, x => x.ScriptPubKey == destination);
			Assert.Equal(Money.Coins(0.13m), destinationutput.Value + result.Fee);
		}

		[Fact]
		public void InsufficientAllowedCoins()
		{
			var transactionFactory = CreateTransactionFactory(new[]{
				("Pablo", 0, 0.01m, confirmed: true, anonymitySet: 1),
				("Jean",  1, 0.08m, confirmed: true, anonymitySet: 1)
			});

			var allowedCoins = new[] {
				transactionFactory.Coins.Single(x=>x.Label.ToString() == "Pablo")
			}.ToArray();

			var amount = Money.Coins(0.5m);  // it is not enough
			var payment = new PaymentIntent(new Key().ScriptPubKey, amount);

			var ex = Assert.Throws<InsufficientBalanceException>(() =>
				transactionFactory.BuildTransaction(payment, new FeeRate(2m), allowedCoins.Select(x => x.GetTxoRef())));

			Assert.Equal(ex.Minimum, amount);
			Assert.Equal(ex.Actual, allowedCoins[0].Amount);
		}

		[Fact]
		public void SpendWholeCoinsEvenWhenNotAllowed()
		{
			var transactionFactory = CreateTransactionFactory(new[]{
				("Pablo",  0, 0.01m, confirmed: false, anonymitySet: 50),
				("Daniel", 1, 0.02m, confirmed: false, anonymitySet: 1),
				("Daniel", 1, 0.04m, confirmed: true, anonymitySet: 1),
				("Maria",  2, 0.08m, confirmed: true, anonymitySet: 100),
			});

			// Selecting 0.08 + 0.02 should be enough but it has to select 0.02 too because it is the same address
			var destination = new Key().ScriptPubKey;
			var payment = new PaymentIntent(new Key().ScriptPubKey, Money.Coins(0.095m));
			var feeRate = new FeeRate(2m);
			var coins = transactionFactory.Coins;
			// the allowed coins contain enough money but one of those has the same script that
			// one unselected coins. That unselected coin has to be spent too.
			var allowedInputs = new[] {
				coins.Single(x=>x.Amount == Money.Coins(0.08m)).GetTxoRef(),
				coins.Single(x=>x.Amount == Money.Coins(0.02m)).GetTxoRef()
			}.ToArray();
			var result = transactionFactory.BuildTransaction(payment, feeRate, allowedInputs);

			Assert.True(result.Signed);
			Assert.Equal(Money.Coins(0.14m), result.SpentCoins.Select(x => x.Amount).Sum());
			Assert.Equal(3, result.SpentCoins.Count());
			var danielCoin = coins.Where(x => x.Label.ToString() == "Daniel").ToArray();
			Assert.Contains(danielCoin[0], result.SpentCoins);
			Assert.Contains(danielCoin[1], result.SpentCoins);
		}

		[Fact]
		public void DoNotSignWatchOnly()
		{
			var transactionFactory = CreateTransactionFactory(new[]{
				("Pablo", 0, 1m, confirmed: true, anonymitySet: 1),
			}, watchOnly: true);

			var payment = new PaymentIntent(new Key().ScriptPubKey, MoneyRequest.CreateAllRemaining(subtractFee: true));

			var result = transactionFactory.BuildTransaction(payment, new FeeRate(44.25m));
			Assert.Single(result.OuterWalletOutputs);
			Assert.False(result.Signed);
		}

		private TransactionFactory CreateTransactionFactory(
			IEnumerable<(string Label, int KeyIndex, decimal Amount, bool Confirmed, int AnonymitySet)> coins,
			bool allowUnconfirmed = true,
			bool watchOnly = false)
		{
			var (password, keyManager) = watchOnly ? WatchOnlyKeyManager() : DefaultKeyManager();

			keyManager.AssertCleanKeysIndexed();

			var keys = keyManager.GetKeys().Take(10).ToArray();
			var scoins = coins.Select(x => Coin(x.Label, keys[x.KeyIndex], x.Amount, x.Confirmed, x.AnonymitySet)).ToList();
			return new TransactionFactory(Network.Main, keyManager, scoins, password, allowUnconfirmed);
		}

		private static (string, KeyManager) DefaultKeyManager()
		{
			var password = "blahblahblah";
			return (password, KeyManager.CreateNew(out var _, password));
		}

		private static (string, KeyManager) WatchOnlyKeyManager()
		{
			var (password, keyManager) = DefaultKeyManager();
			return (password, KeyManager.CreateNewWatchOnly(keyManager.ExtPubKey));
		}

		private static SmartCoin Coin(string label, HdPubKey pubKey, decimal amount, bool confirmed = true, int anonymitySet = 1)
		{
			var randomIndex = new Func<int>(() => new Random().Next(0, 200));
			var height = confirmed ? new Height(randomIndex()) : Height.Mempool;
			var slabel = new SmartLabel(label);
			var spentOutput = new[]{
				new TxoRef(RandomUtils.GetUInt256(), (uint)randomIndex())
			};
			pubKey.SetLabel(slabel);
			return new SmartCoin(RandomUtils.GetUInt256(), (uint)randomIndex(), pubKey.P2wpkhScript, Money.Coins(amount), spentOutput, height, false, anonymitySet, false, slabel, pubKey: pubKey);
		}
	}
}
