﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Nop.Core;
using Nop.Core.Caching;
using Nop.Core.Domain.Catalog;
using Nop.Data;
using Nop.Services.Customers;
using Nop.Services.Security;
using Nop.Services.Seo;
using Nop.Services.Stores;

namespace Nop.Services.Catalog
{
    /// <summary>
    /// Product tag service
    /// </summary>
    public partial class ProductTagService : IProductTagService
    {
        #region Fields

        private readonly CatalogSettings _catalogSettings;
        private readonly IAclService _aclService;
        private readonly ICustomerService _customerService;
        private readonly IRepository<Product> _productRepository;
        private readonly IRepository<ProductProductTagMapping> _productProductTagMappingRepository;
        private readonly IRepository<ProductTag> _productTagRepository;
        private readonly IStaticCacheManager _staticCacheManager;
        private readonly IStoreMappingService _storeMappingService;
        private readonly IUrlRecordService _urlRecordService;
        private readonly IWorkContext _workContext;

        #endregion

        #region Ctor

        public ProductTagService(CatalogSettings catalogSettings,
            IAclService aclService,
            ICustomerService customerService,
            IRepository<Product> productRepository,
            IRepository<ProductProductTagMapping> productProductTagMappingRepository,
            IRepository<ProductTag> productTagRepository,
            IStaticCacheManager staticCacheManager,
            IStoreMappingService storeMappingService,
            IUrlRecordService urlRecordService,
            IWorkContext workContext)
        {
            _catalogSettings = catalogSettings;
            _aclService = aclService;
            _customerService = customerService;
            _productRepository = productRepository;
            _productProductTagMappingRepository = productProductTagMappingRepository;
            _productTagRepository = productTagRepository;
            _staticCacheManager = staticCacheManager;
            _storeMappingService = storeMappingService;
            _urlRecordService = urlRecordService;
            _workContext = workContext;
        }

        #endregion

        #region Utilities

        /// <summary>
        /// Delete a product-product tag mapping
        /// </summary>
        /// <param name="productId">Product identifier</param>
        /// <param name="productTagId">Product tag identifier</param>
        protected virtual async Task DeleteProductProductTagMappingAsync(int productId, int productTagId)
        {
            var mappingRecord = await _productProductTagMappingRepository.Table
                .FirstOrDefaultAsync(pptm => pptm.ProductId == productId && pptm.ProductTagId == productTagId);

            if (mappingRecord is null)
                throw new Exception("Mapping record not found");

            await _productProductTagMappingRepository.DeleteAsync(mappingRecord);
        }

        /// <summary>
        /// Filter hidden entries according to constraints if any
        /// </summary>
        /// <param name="query">Query to filter</param>
        /// <param name="storeId">A store identifier</param>
        /// <param name="customerRolesIds">Identifiers of customer's roles</param>
        /// <returns>Filtered query</returns>
        protected virtual async Task<IQueryable<TEntity>> FilterHiddenEntriesAsync<TEntity>(IQueryable<TEntity> query,
            int storeId, int[] customerRolesIds)
            where TEntity : Product
        {
            //filter unpublished entries
            query = query.Where(entry => entry.Published);

            //apply store mapping constraints
            if (!_catalogSettings.IgnoreStoreLimitations && await _storeMappingService.IsEntityMappingExistsAsync<TEntity>(storeId))
                query = query.Where(_storeMappingService.ApplyStoreMapping<TEntity>(storeId));

            //apply ACL constraints
            if (!_catalogSettings.IgnoreAcl && await _aclService.IsEntityAclMappingExistAsync<TEntity>(customerRolesIds))
                query = query.Where(_aclService.ApplyAcl<TEntity>(customerRolesIds));

            return query;
        }

        /// <summary>
        /// Get product count for each of existing product tag
        /// </summary>
        /// <param name="storeId">Store identifier</param>
        /// <param name="showHidden">A value indicating whether to show hidden records</param>
        /// <returns>Dictionary of "product tag ID : product count"</returns>
        protected virtual async Task<Dictionary<int, int>> GetProductCountAsync(int storeId, bool showHidden)
        {
            var customer = await _workContext.GetCurrentCustomerAsync();
            var customerRolesIds = await _customerService.GetCustomerRoleIdsAsync(customer);

            var key = _staticCacheManager.PrepareKeyForDefaultCache(NopCatalogDefaults.ProductTagCountCacheKey, storeId, customerRolesIds, showHidden);

            return await _staticCacheManager.GetAsync(key, async () =>
            {
                var query = _productProductTagMappingRepository.Table;

                if (!showHidden)
                {
                    var productsQuery = await FilterHiddenEntriesAsync(_productRepository.Table, storeId, customerRolesIds);
                    query = query.Where(pc => productsQuery.Any(p => !p.Deleted && pc.ProductId == p.Id));
                }

                var pTagCount = from pt in _productTagRepository.Table
                                join ptm in query on pt.Id equals ptm.ProductTagId into ptmDefaults
                                from ptm in ptmDefaults.DefaultIfEmpty()
                                group pt by pt.Id into ptGrouped
                                select new
                                {
                                    ProductTagId = ptGrouped.Key,
                                    ProductCount = ptGrouped.Count()
                                };

                return pTagCount.ToDictionary(item => item.ProductTagId, item => item.ProductCount);
            });
        }

        /// <summary>
        /// Indicates whether a product tag exists
        /// </summary>
        /// <param name="product">Product</param>
        /// <param name="productTagId">Product tag identifier</param>
        /// <returns>Result</returns>
        protected virtual async Task<bool> ProductTagExistsAsync(Product product, int productTagId)
        {
            if (product == null)
                throw new ArgumentNullException(nameof(product));

            return await _productProductTagMappingRepository.Table
                .AnyAsync(pptm => pptm.ProductId == product.Id && pptm.ProductTagId == productTagId);
        }

        /// <summary>
        /// Gets product tag by name
        /// </summary>
        /// <param name="name">Product tag name</param>
        /// <returns>Product tag</returns>
        protected virtual async Task<ProductTag> GetProductTagByNameAsync(string name)
        {
            var query = from pt in _productTagRepository.Table
                where pt.Name == name
                select pt;

            var productTag = await query.FirstOrDefaultAsync();
            return productTag;
        }

        /// <summary>
        /// Inserts a product tag
        /// </summary>
        /// <param name="productTag">Product tag</param>
        protected virtual async Task InsertProductTagAsync(ProductTag productTag)
        {
            await _productTagRepository.InsertAsync(productTag);
        }

        #endregion

        #region Methods

        /// <summary>
        /// Delete a product tag
        /// </summary>
        /// <param name="productTag">Product tag</param>
        public virtual async Task DeleteProductTagAsync(ProductTag productTag)
        {
            await _productTagRepository.DeleteAsync(productTag);
        }

        /// <summary>
        /// Delete product tags
        /// </summary>
        /// <param name="productTags">Product tags</param>
        public virtual async Task DeleteProductTagsAsync(IList<ProductTag> productTags)
        {
            if (productTags == null)
                throw new ArgumentNullException(nameof(productTags));

            foreach (var productTag in productTags)
                await DeleteProductTagAsync(productTag);
        }

        /// <summary>
        /// Gets all product tags
        /// </summary>
        /// <param name="tagName">Tag name</param>
        /// <returns>Product tags</returns>
        public virtual async Task<IList<ProductTag>> GetAllProductTagsAsync(string tagName = null)
        {
            var allProductTags = await _productTagRepository.GetAllAsync(query => query, getCacheKey: cache => default);

            if (!string.IsNullOrEmpty(tagName))
                allProductTags = allProductTags.Where(tag => tag.Name.Contains(tagName)).ToList();

            return allProductTags;
        }

        /// <summary>
        /// Gets all product tags by product identifier
        /// </summary>
        /// <param name="productId">Product identifier</param>
        /// <returns>Product tags</returns>
        public virtual async Task<IList<ProductTag>> GetAllProductTagsByProductIdAsync(int productId)
        {
            var productTags = await _productTagRepository.GetAllAsync(query =>
            {
                return from pt in query
                       join ppt in _productProductTagMappingRepository.Table on pt.Id equals ppt.ProductTagId
                       where ppt.ProductId == productId
                       orderby pt.Id
                       select pt;
            }, cache => cache.PrepareKeyForDefaultCache(NopCatalogDefaults.ProductTagsByProductCacheKey, productId));

            return productTags;
        }

        /// <summary>
        /// Gets product tag
        /// </summary>
        /// <param name="productTagId">Product tag identifier</param>
        /// <returns>Product tag</returns>
        public virtual async Task<ProductTag> GetProductTagByIdAsync(int productTagId)
        {
            return await _productTagRepository.GetByIdAsync(productTagId, cache => default);
        }

        /// <summary>
        /// Gets product tags
        /// </summary>
        /// <param name="productTagIds">Product tags identifiers</param>
        /// <returns>Product tags</returns>
        public virtual async Task<IList<ProductTag>> GetProductTagsByIdsAsync(int[] productTagIds)
        {
            return await _productTagRepository.GetByIdsAsync(productTagIds);
        }
        
        /// <summary>
        /// Inserts a product-product tag mapping
        /// </summary>
        /// <param name="tagMapping">Product-product tag mapping</param>
        public virtual async Task InsertProductProductTagMappingAsync(ProductProductTagMapping tagMapping)
        {
            await _productProductTagMappingRepository.InsertAsync(tagMapping);
        }
        
        /// <summary>
        /// Updates the product tag
        /// </summary>
        /// <param name="productTag">Product tag</param>
        public virtual async Task UpdateProductTagAsync(ProductTag productTag)
        {
            if (productTag == null)
                throw new ArgumentNullException(nameof(productTag));

            await _productTagRepository.UpdateAsync(productTag);

            var seName = await _urlRecordService.ValidateSeNameAsync(productTag, string.Empty, productTag.Name, true);
            await _urlRecordService.SaveSlugAsync(productTag, seName, 0);
        }

        /// <summary>
        /// Get number of products
        /// </summary>
        /// <param name="productTagId">Product tag identifier</param>
        /// <param name="storeId">Store identifier</param>
        /// <param name="showHidden">A value indicating whether to show hidden records</param>
        /// <returns>Number of products</returns>
        public virtual async Task<int> GetProductCountAsync(int productTagId, int storeId, bool showHidden = false)
        {
            var dictionary = await GetProductCountAsync(storeId, showHidden);
            if (dictionary.ContainsKey(productTagId))
                return dictionary[productTagId];

            return 0;
        }

        /// <summary>
        /// Update product tags
        /// </summary>
        /// <param name="product">Product for update</param>
        /// <param name="productTags">Product tags</param>
        public virtual async Task UpdateProductTagsAsync(Product product, string[] productTags)
        {
            if (product == null)
                throw new ArgumentNullException(nameof(product));

            //product tags
            var existingProductTags = await GetAllProductTagsByProductIdAsync(product.Id);
            var productTagsToRemove = new List<ProductTag>();
            foreach (var existingProductTag in existingProductTags)
            {
                var found = false;
                foreach (var newProductTag in productTags)
                {
                    if (!existingProductTag.Name.Equals(newProductTag, StringComparison.InvariantCultureIgnoreCase))
                        continue;

                    found = true;
                    break;
                }

                if (!found)
                    productTagsToRemove.Add(existingProductTag);
            }

            foreach (var productTag in productTagsToRemove)
                await DeleteProductProductTagMappingAsync(product.Id, productTag.Id);

            foreach (var productTagName in productTags)
            {
                ProductTag productTag;
                var productTag2 = await GetProductTagByNameAsync(productTagName);
                if (productTag2 == null)
                {
                    //add new product tag
                    productTag = new ProductTag
                    {
                        Name = productTagName
                    };
                    await InsertProductTagAsync(productTag);
                }
                else
                    productTag = productTag2;

                if (!await ProductTagExistsAsync(product, productTag.Id))
                    await InsertProductProductTagMappingAsync(new ProductProductTagMapping { ProductTagId = productTag.Id, ProductId = product.Id });

                var seName = await _urlRecordService.ValidateSeNameAsync(productTag, string.Empty, productTag.Name, true);
                await _urlRecordService.SaveSlugAsync(productTag, seName, 0);
            }

            //cache
            await _staticCacheManager.RemoveByPrefixAsync(NopEntityCacheDefaults<ProductTag>.Prefix);
        }

        #endregion
    }
}