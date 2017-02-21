namespace MiNET.Items.Enchantments
{
	public abstract class Enchantment
	{
		public EnchantmentType Id;
		public short Level;

		protected Enchantment(short level)
		{
			Level = level;
		}
	}
}