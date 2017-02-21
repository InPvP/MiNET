namespace MiNET.Items.Enchantments
{
	public class Smite : Enchantment
	{
		public Smite(short level = 1) : base(level)
		{
			Id = EnchantmentType.Smite;
		}
	}
}