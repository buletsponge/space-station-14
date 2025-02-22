using System.Linq;
using System.Threading.Tasks;
using Content.Server.Access.Components;
using Content.Server.Hands.Components;
using Content.Server.Inventory.Components;
using Content.Server.Items;
using Content.Server.PDA;
using Content.Shared.Containers.ItemSlots;
using NUnit.Framework;
using Robust.Server.Player;
using Robust.Shared.GameObjects;

namespace Content.IntegrationTests.Tests.PDA
{
    public class PDAExtensionsTests : ContentIntegrationTest
    {
        private const string IdCardDummy = "DummyIdCard";
        private const string PdaDummy = "DummyPda";

        private static readonly string Prototypes = $@"
- type: entity
  id: {IdCardDummy}
  name: {IdCardDummy}
  components:
  - type: IdCard
  - type: Item

- type: entity
  id: {PdaDummy}
  name: {PdaDummy}
  components:
  - type: PDA
    idSlot:
      name: ID Card
      whitelist:
        components:
        - IdCard
  - type: Item";

        [Test]
        public async Task PlayerGetIdComponent()
        {
            var clientOptions = new ClientIntegrationOptions
            {
                ExtraPrototypes = Prototypes
            };

            var serverOptions = new ServerIntegrationOptions
            {
                ExtraPrototypes = Prototypes
            };

            var (client, server) = await StartConnectedServerClientPair(clientOptions, serverOptions);

            await Task.WhenAll(client.WaitIdleAsync(), server.WaitIdleAsync());

            var sPlayerManager = server.ResolveDependency<IPlayerManager>();
            var sEntityManager = server.ResolveDependency<IEntityManager>();

            await server.WaitAssertion(() =>
            {
                var player = sPlayerManager.Sessions.Single().AttachedEntity.GetValueOrDefault();

                Assert.That(player != default);

                // The player spawns with an ID on by default
                Assert.NotNull(player.GetHeldId());
                Assert.True(player.TryGetHeldId(out var id));
                Assert.NotNull(id);

                // Put PDA in hand
                var dummyPda = sEntityManager.SpawnEntity(PdaDummy, sEntityManager.GetComponent<TransformComponent>(player).MapPosition);
                var pdaItemComponent = sEntityManager.GetComponent<ItemComponent>(dummyPda);
                sEntityManager.GetComponent<HandsComponent>(player).PutInHand(pdaItemComponent);

                var pdaComponent = sEntityManager.GetComponent<PDAComponent>(dummyPda);
                var pdaIdCard = sEntityManager.SpawnEntity(IdCardDummy, sEntityManager.GetComponent<TransformComponent>(player).MapPosition);

                var itemSlots = sEntityManager.GetComponent<ItemSlotsComponent>(dummyPda);
                sEntityManager.EntitySysManager.GetEntitySystem<ItemSlotsSystem>()
                    .TryInsert(dummyPda, pdaComponent.IdSlot, pdaIdCard);
                var pdaContainedId = pdaComponent.ContainedID;

                // The PDA in the hand should be found first
                Assert.NotNull(player.GetHeldId());
                Assert.True(player.TryGetHeldId(out id));

                Assert.NotNull(id);
                Assert.That(id, Is.EqualTo(pdaContainedId));

                // Put ID card in hand
                var idDummy = sEntityManager.SpawnEntity(IdCardDummy, sEntityManager.GetComponent<TransformComponent>(player).MapPosition);
                var idItemComponent = sEntityManager.GetComponent<ItemComponent>(idDummy);
                sEntityManager.GetComponent<HandsComponent>(player).PutInHand(idItemComponent);

                var idCardComponent = sEntityManager.GetComponent<IdCardComponent>(idDummy);

                // The ID in the hand should be found first
                Assert.NotNull(player.GetHeldId());
                Assert.True(player.TryGetHeldId(out id));
                Assert.NotNull(id);
                Assert.That(id, Is.EqualTo(idCardComponent));

                // Remove all IDs and PDAs
                var inventory = sEntityManager.GetComponent<InventoryComponent>(player);

                foreach (var slot in inventory.Slots)
                {
                    var item = inventory.GetSlotItem(slot);

                    if (item == null)
                    {
                        continue;
                    }

                    if (sEntityManager.HasComponent<PDAComponent>(item.Owner))
                    {
                        inventory.ForceUnequip(slot);
                    }
                }

                var hands = sEntityManager.GetComponent<HandsComponent>(player);

                hands.Drop(dummyPda, false);
                hands.Drop(idDummy, false);

                // No ID
                Assert.Null(player.GetHeldId());
                Assert.False(player.TryGetHeldId(out id));
                Assert.Null(id);
            });
        }
    }
}
