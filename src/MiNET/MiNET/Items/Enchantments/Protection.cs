namespace MiNET.Items.Enchantments
{
	public class Protection : Enchantment
	{
		public Protection(short level = 1) : base(level)
		{
			Id = EnchantmentType.Protection;
		}
	}
}