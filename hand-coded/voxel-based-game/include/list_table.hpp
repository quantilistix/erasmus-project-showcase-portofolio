#pragma once

// Dependencies
#include <pch.hpp>

// Static sized lock-free list table
template <typename key, typename value>
class list_table
{
    // Public members
public:
    // Construct new list table
    list_table(unsigned long size)
    {
        table_size = size;
        key_table.resize(table_size);
        value_table.resize(table_size);

        // Allocate arrays of atomics
        exists = std::make_unique<std::atomic<bool>[]>(table_size);
        locked = std::make_unique<std::atomic<bool>[]>(table_size);

        for (unsigned long i = 0; i < table_size; i++)
        {

            // Clear the values first
            key_table[i] = key();
            value_table[i] = value();

            // Use release ordering to ensure the above writes are visible
            exists[i].store(false, std::memory_order_relaxed);
            locked[i].store(false, std::memory_order_relaxed);
        }
    }

    ~list_table()
    {
        // delete[] key_table;
        // delete[] value_table;
        // delete[] exists;
        // delete[] locked;
    }

    // [] Operator for reading and writing into the table (uses proxy implementation)
    // Approach 1: Return a reference that can be both read and written to
    // Note: This has a race condition between finding/creating the slot and writing to it
    value &operator[](const key &Key)
    {
        // First try to find existing element
        for (unsigned long i = 0; i < table_size; i++)
        {
            if (exists[i].load(std::memory_order_acquire))
            {
                if (key_table[i] == Key)
                {
                    // Wait if the element is locked
                    while (locked[i].load(std::memory_order_acquire))
                    {
                        std::this_thread::yield();
                    }
                    return value_table[i];
                }
            }
        }

        // If not found, create new element
        for (unsigned long i = 0; i < table_size; i++)
        {
            bool expected = false;
            if (exists[i].compare_exchange_strong(expected, true,
                                                  std::memory_order_acq_rel))
            {
                key_table[i] = Key;
                value_table[i] = value(); // Default construct
                return value_table[i];
            }
        }
        throw std::out_of_range("Table is full");
    }

    // Returns the pointer of the value given the key
    value *find(key Key)
    {
        for (unsigned long i = 0; i < table_size; i++)
        {
            if (exists[i].load(std::memory_order_acquire))
            {
                if (key_table[i] == Key)
                {
                    while (locked[i].load(std::memory_order_acquire))
                    {
                        std::this_thread::yield();
                    }
                    return &value_table[i];
                }
            }
        }
        return end();
    }

    // Returns the pointer of the value given the key
    std::pair<key, value> iterate(unsigned long i)
    {
        if (exists[i].load(std::memory_order_acquire))
        {
            return {key_table[i], value_table[i]};
        }
        return std::pair<key, value>(key(), value());
    }

    // Helper method to lock an element for exclusive access
    bool lock_element(const key &Key)
    {
        for (unsigned long i = 0; i < table_size; i++)
        {
            if (exists[i].load(std::memory_order_acquire) && key_table[i] == Key)
            {
                bool expected = false;
                return locked[i].compare_exchange_strong(expected, true,
                                                         std::memory_order_acq_rel);
            }
        }
        return false;
    }

    // Helper method to unlock an element
    void unlock_element(const key &Key)
    {
        for (unsigned long i = 0; i < table_size; i++)
        {
            if (exists[i].load(std::memory_order_acquire) && key_table[i] == Key)
            {
                locked[i].store(false, std::memory_order_release);
                return;
            }
        }
    }

    // Returns the beginning pointer of the table
    value *begin()
    {
        return *value_table[0];
    }
    // Returns the ending pointer of the table
    value *end()
    {
        return &value_table[table_size];
    }

    // Inserts a new element into the list table and returns false if successful. If not successful, it returns true.
    bool insert(key Key, value Value)
    {
        for (unsigned long i = 0; i < table_size; i++)
        {
            bool expected = false;
            if (exists[i].compare_exchange_strong(expected, true, std::memory_order_acq_rel))
            {
                key_table[i] = Key;
                value_table[i] = Value;
                return false; // Success
            }
        }
        return true; // Table is full
    }

    // Deletes an element from the list table and returns false if successful. If not successful, it returns true.
    // ```WARNING!!!``` - If the value is a pointer, the pointer will be deleted, but not the value at that pointer!
    // Delete the value using ```free()``` or ```delete``` before using the ```.erase()``` method!
    bool erase(key Key)
    {
        for (unsigned long i = 0; i < table_size; i++)
        {
            // First check if the element exists
            if (exists[i].load(std::memory_order_acquire))
            {
                if (key_table[i] == Key)
                {
                    // Try to lock the element for deletion
                    bool expected = false;
                    if (locked[i].compare_exchange_strong(expected, true,
                                                          std::memory_order_acq_rel))
                    {
                        // Double-check that the element still exists and matches
                        // (could have been modified between our check and lock)
                        if (exists[i].load(std::memory_order_acquire) &&
                            key_table[i] == Key)
                        {

                            // Clear the values first
                            key_table[i] = key();
                            value_table[i] = value();

                            // Use release ordering to ensure the above writes are visible
                            exists[i].store(false, std::memory_order_release);
                            locked[i].store(false, std::memory_order_release);
                            return false; // Successfully erased
                        }
                        // If we got here, the element changed before we could delete it
                        locked[i].store(false, std::memory_order_release);
                    }
                }
            }
        }
        return true; // Element not found
    }

    // Deletes an element from the list table and returns false if successful. If not successful, it returns true.
    // ```WARNING!!!``` - If the value is a pointer, the pointer will be deleted, but not the value at that pointer!
    // Delete the value using ```free()``` or ```delete``` before using the ```.erase()``` method!
    bool erase(value *ptr)
    {
        unsigned long i = ((unsigned long)ptr - (unsigned long)&value_table[0]) / sizeof(value);
        // First check if the element exists
        if (exists[i].load(std::memory_order_acquire))
        {

            // Try to lock the element for deletion
            bool expected = false;
            if (locked[i].compare_exchange_strong(expected, true,
                                                  std::memory_order_acq_rel))
            {
                // Double-check that the element still exists and matches
                // (could have been modified between our check and lock)
                if (exists[i].load(std::memory_order_acquire))
                {

                    // Clear the values first
                    key_table[i] = key();
                    value_table[i] = value();

                    // Use release ordering to ensure the above writes are visible
                    exists[i].store(false, std::memory_order_release);
                    locked[i].store(false, std::memory_order_release);
                    return false; // Successfully erased
                }
                // If we got here, the element changed before we could delete it
                locked[i].store(false, std::memory_order_release);
            }
        }
        return true; // Element not found
    }

    // Just clears the entire table
    void clear()
    {
        for (unsigned long i = 0; i < table_size; i++)
        {
            // First check if the element exists
            if (exists[i].load(std::memory_order_acquire))
            {
                // Try to lock the element for deletion
                bool expected = false;
                if (locked[i].compare_exchange_strong(expected, true,
                                                      std::memory_order_acq_rel))
                {
                    // Double-check that the element still exists and matches
                    // (could have been modified between our check and lock)
                    if (exists[i].load(std::memory_order_acquire))
                    {

                        // Clear the values first
                        key_table[i] = key();
                        value_table[i] = value();

                        // Use release ordering to ensure the above writes are visible
                        exists[i].store(false, std::memory_order_release);
                        locked[i].store(false, std::memory_order_release);
                    }
                    // If we got here, the element changed before we could delete it
                    locked[i].store(false, std::memory_order_release);
                }
            }
        }
    }

    void resize(unsigned long size)
    {
        // Save old data
        auto old_exists = std::move(exists);
        auto old_locked = std::move(locked);
        auto old_size = table_size;

        // Set the new table size
        table_size = size;
        // Resize vectors
        key_table.resize(table_size);
        value_table.resize(table_size);

        // Resize atomics
        // Allocate new atomic arrays
        exists = std::make_unique<std::atomic<bool>[]>(table_size);
        locked = std::make_unique<std::atomic<bool>[]>(table_size);

        // Initialize new arrays
        for (unsigned long i = 0; i < table_size; i++)
        {
            exists[i].store(false, std::memory_order_relaxed);
            locked[i].store(false, std::memory_order_relaxed);
        }

        // Copy old data if growing
        for (unsigned long i = 0; i < std::min(old_size, table_size); i++)
        {
            exists[i].store(old_exists[i].load(std::memory_order_relaxed),
                            std::memory_order_relaxed);
            locked[i].store(old_locked[i].load(std::memory_order_relaxed),
                            std::memory_order_relaxed);
        }
    }

    // Returns the size of the table
    unsigned long
    capacity()
    {
        return table_size;
    }

    // Private members
private:
    // The table size
    unsigned long table_size;
    // The key table (the table that stores the keys)
    std::vector<key> key_table;
    // The value table (the table that stores the values)
    std::vector<value> value_table;
    // The exists boolean table (the table that stores wheather the element exists or not)
    std::unique_ptr<std::atomic<bool>[]> exists;
    // The lock boolean tables (the table that stores wheather the element is locked or not)
    std::unique_ptr<std::atomic<bool>[]> locked;

    // Approach 2: Provide an atomic_reference wrapper
    // This is more thread-safe but requires explicit locking
    class atomic_reference
    {
    public:
        atomic_reference(list_table *parent, const key &k)
            : parent_(parent), key_(k)
        {
            parent_->lock_element(key_);
        }

        ~atomic_reference()
        {
            parent_->unlock_element(key_);
        }

        // Assignment operator
        atomic_reference &operator=(const value &v)
        {
            value &ref = parent_->get_value_unsafe(key_);
            ref = v;
            return *this;
        }

        // Conversion operator to value
        operator value() const
        {
            return parent_->get_value_unsafe(key_);
        }

    private:
        list_table *parent_;
        key key_;
    };

    // Helper method for atomic_reference to access values directly
    value &get_value_unsafe(const key &Key)
    {
        for (unsigned long i = 0; i < table_size; i++)
        {
            if (exists[i].load(std::memory_order_acquire) && key_table[i] == Key)
            {
                return value_table[i];
            }
        }
        throw std::out_of_range("Key not found");
    }
};