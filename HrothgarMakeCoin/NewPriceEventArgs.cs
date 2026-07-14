using System;

namespace HrothgarMakeCoin
{
  internal sealed class NewPriceEventArgs(int newPrice) : EventArgs
  {
    public int NewPrice { get; } = newPrice;
  }
}