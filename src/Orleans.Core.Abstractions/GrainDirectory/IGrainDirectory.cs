using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.GrainDirectory
{
    /// <summary>
    /// Interface for grain directory implementations
    /// </summary>
    public interface IGrainDirectory
    {
        /// <summary>
        /// Register a <see cref="GrainAddress"/> entry in the directory.
        /// Only one <see cref="GrainAddress"/> per <see cref="GrainAddress.GrainId"/> can be registered. If there is already an
        /// existing entry, the directory will not override it.
        /// </summary>
        /// <param name="address">The <see cref="GrainAddress"/> to register</param>
        /// <returns>The <see cref="GrainAddress"/> that is effectively registered in the directory.</returns>
        Task<GrainAddress> Register(GrainAddress address);

        /// <summary>
        /// Unregister a <see cref="GrainAddress"/> entry in the directory.
        /// </summary>
        /// <param name="address">The <see cref="GrainAddress"/> to unregister</param>
        Task Unregister(GrainAddress address);

        /// <summary>
        /// Lookup for a <see cref="GrainAddress"/> for a given Grain ID.
        /// </summary>
        /// <param name="grainId">The Grain ID to lookup</param>
        /// <returns>The <see cref="GrainAddress"/> entry found in the directory, if any</returns>
        Task<GrainAddress> Lookup(string grainId);

        /// <summary>
        /// Unregister from the directory all entries that point to one of the silo in argument.
        /// Can be a NO-OP depending on the implementation.
        /// </summary>
        /// <param name="siloAddresses">The silos to be removed from the directory</param>
        Task UnregisterSilos(List<string> siloAddresses);
    }

    /// <summary>
    /// Represents an entry in a <see cref="IGrainDirectory"/>
    /// </summary>
    public class GrainAddress
    {
        /// <summary>
        /// Identifier of the Grain
        /// </summary>
        public string GrainId { get; set; }

        /// <summary>
        /// Id of the specific Grain activation
        /// </summary>
        public string ActivationId { get; set; }

        /// <summary>
        /// Address of the silo where the grain activation lives
        /// </summary>
        public string SiloAddress { get; set; }

        /// <summary>
        /// The membership version at the time this registration was created.
        /// </summary>
        public long MembershipVersion { get; set; }

        public override bool Equals(object obj)
        {
            return obj is GrainAddress address &&
                   this.SiloAddress == address.SiloAddress &&
                   this.Matches(address) &&
                   this.MembershipVersion == address.MembershipVersion;
        }

        /// <summary>
        /// Two grain addresses match if they are equal ignoring their <see cref="MembershipVersion"/> value.
        /// </summary>
        /// <param name="address"> The other GrainAddress to compare this one with.</param>
        /// <returns> Returns <c>true</c> if the two GrainAddress are considered to match</returns>
        public bool Matches(GrainAddress address)
        {
            return this.SiloAddress == address.SiloAddress &&
                   this.GrainId == address.GrainId &&
                   this.ActivationId == address.ActivationId;
        }

        public override int GetHashCode()
        {
            var hashCode = 1043893337;
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(this.SiloAddress);
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(this.GrainId);
            hashCode = hashCode * -1521134295 + EqualityComparer<string>.Default.GetHashCode(this.ActivationId);
            return hashCode;
        }
    }
}
