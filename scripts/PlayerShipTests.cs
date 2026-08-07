using Microsoft.VisualStudio.TestTools.UnitTesting;
using ColdOrbit.SimCore;

namespace ColdOrbit.Tests
{
    [TestClass]
    public class PlayerShipTests
    {
        [TestMethod]
        public void TestThrustForce_DefaultValue()
        {
            PlayerShip ship = new PlayerShip();
            Assert.AreEqual(4000f, ship.ThrustForce, "Default ThrustForce is incorrect.");
        }

        [TestMethod]
        public void TestRcsForce_DefaultValue()
        {
            PlayerShip ship = new PlayerShip();
            Assert.AreEqual(800f, ship.RcsForce, "Default RcsForce is incorrect.");
        }

        [TestMethod]
        public void TestTorqueForce_DefaultValue()
        {
            PlayerShip ship = new PlayerShip();
            Assert.AreEqual(800f, ship.TorqueForce, "Default TorqueForce is incorrect.");
        }
    }
}